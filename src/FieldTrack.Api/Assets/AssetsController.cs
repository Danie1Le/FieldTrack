using FieldTrack.Api.Auth;
using FieldTrack.Api.Live;
using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Assets;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FieldTrack.Api.Assets;

/// <summary>
/// The tracked field equipment. Everyone signed in reads Assets; only Admins change them (spec #21, Auth).
/// </summary>
[ApiController]
[Route("assets")]
[Authorize(Policy = Policies.Read)]
public sealed class AssetsController(FieldTrackDbContext db, IReadingHistory readingHistory, TimeProvider time, LivePush live) : ControllerBase
{
    /// <summary>Lists Assets by name, leaving out Archived ones unless <paramref name="includeArchived"/> is true.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<AssetResponse>> List(bool includeArchived, CancellationToken cancellationToken) =>
        await db.Assets
            .Where(asset => includeArchived || !asset.Archived)
            .OrderBy(asset => asset.Name)
            .Select(asset => ToResponse(asset))
            .ToListAsync(cancellationToken);

    /// <summary>Reads one Asset, Archived ones too, so a history view can still name it.</summary>
    /// <response code="404">No Asset has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssetResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Assets.Where(asset => asset.Id == id).Select(asset => ToResponse(asset)).SingleOrDefaultAsync(cancellationToken) is { } asset
            ? asset
            : NotFound();

    /// <summary>Creates an Asset.</summary>
    /// <response code="400">A missing name, an unknown Asset Type or an unknown Asset Group.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AssetResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AssetResponse>> Create(AssetRequest request, CancellationToken cancellationToken)
    {
        if (await IsUnknownAssetGroupAsync(request.AssetGroupId, cancellationToken))
        {
            return UnknownAssetGroup();
        }

        var asset = new Asset { Name = request.Name, Type = request.Type!.Value, AssetGroupId = request.AssetGroupId };
        db.Assets.Add(asset);
        if (!await TrySaveAsync(cancellationToken))
        {
            return UnknownAssetGroup();
        }

        await live.PushAsync([asset.Id], [], [], cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(asset));
    }

    /// <summary>Renames an Asset, changes its Asset Type, or moves it into, between or out of Asset Groups.</summary>
    /// <response code="400">A missing name, an unknown Asset Type or an unknown Asset Group.</response>
    /// <response code="404">No Asset has this id.</response>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssetResponse>> Update(Guid id, AssetRequest request, CancellationToken cancellationToken)
    {
        if (await db.Assets.FindAsync([id], cancellationToken) is not { } asset)
        {
            return NotFound();
        }

        if (await IsUnknownAssetGroupAsync(request.AssetGroupId, cancellationToken))
        {
            return UnknownAssetGroup();
        }

        asset.Name = request.Name;
        asset.Type = request.Type!.Value;
        asset.AssetGroupId = request.AssetGroupId;
        if (!await TrySaveAsync(cancellationToken))
        {
            return UnknownAssetGroup();
        }

        await live.PushAsync([id], [], [], cancellationToken);
        return ToResponse(asset);
    }

    /// <summary>
    /// Deletes an Asset with no history outright, with its Attachments, so mistakes are easy to clean
    /// up. An Asset with history (Readings, or Alerts: an Offline Alert needs no Reading) is Archived instead and its
    /// open Attachment ended, which keeps the history and hides it from lists; its own Alert Rules are disabled and its
    /// Active Alerts resolve as Asset archived.
    /// </summary>
    /// <response code="404">No Asset has this id.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // One transaction with the Asset row locked, so the decision and everything archiving
        // changes commit together, and a concurrent delete or edit waits for it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await db.Assets.FromSql($"SELECT * FROM assets WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        // History (GLOSSARY Archived) is Readings or Alerts: an Offline Alert needs no Reading. ADR 0005 sweep: ingest
        // holds the Asset row (FOR KEY SHARE) while it raises an Offline Alert, so this lock waits and sees that Alert.
        var hasHistory = await readingHistory.AssetHasReadingsAsync(id, cancellationToken)
                         || await db.Alerts.AnyAsync(alert => alert.AssetId == id, cancellationToken);
        // Either way the Asset's own rules stop: disabled with it, or deleted with it. Bumped after the history check,
        // which can be slow and would hold up every ingest batch meanwhile (ADR 0005); the Asset lock already keeps
        // ingest from stamping a Reading on it.
        await db.BumpAlertRuleVersionAsync(cancellationToken);
        List<Guid> resolved = [];
        if (hasHistory)
        {
            asset.Archived = true;
            var now = time.GetUtcNow();
            await db.AlertRules.Where(rule => rule.AssetId == id).ExecuteUpdateAsync(set => set.SetProperty(rule => rule.Enabled, false), cancellationToken);
            // Before the Attachment ends, which would resolve them as Device detached.
            resolved = await db.ResolveActiveAlertsOfAssetsAsync([id], ResolveReason.AssetArchived, now, cancellationToken);
            await db.EndOpenAttachmentsAsync(attachment => attachment.AssetId == id, now, cancellationToken);
        }
        else
        {
            // No Readings or Alerts means no history to keep, so its Attachments go with it (#25).
            await db.Attachments.Where(attachment => attachment.AssetId == id).ExecuteDeleteAsync(cancellationToken);
            db.Assets.Remove(asset);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // Archived or deleted, it goes out as removed.
        await live.PushAsync([id], [], resolved, cancellationToken);
        return NoContent();
    }

    private async Task<bool> IsUnknownAssetGroupAsync(Guid? assetGroupId, CancellationToken cancellationToken) =>
        assetGroupId is { } id && !await db.AssetGroups.AnyAsync(group => group.Id == id, cancellationToken);

    // False when the Asset Group was deleted after the check above: the foreign key decides.
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return false;
        }
    }

    private ActionResult UnknownAssetGroup()
    {
        ModelState.AddModelError(nameof(AssetRequest.AssetGroupId), "No Asset Group has this id.");
        return ValidationProblem(ModelState);
    }

    private static AssetResponse ToResponse(Asset asset) =>
        new(asset.Id, asset.Name, asset.Type, asset.AssetGroupId, asset.Archived);
}
