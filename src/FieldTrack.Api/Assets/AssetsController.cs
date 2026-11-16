using FieldTrack.Api.Auth;
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
public sealed class AssetsController(FieldTrackDbContext db) : ControllerBase
{
    /// <summary>Lists Assets by name, leaving out Archived ones unless <paramref name="includeArchived"/> is true.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<AssetResponse>> List(bool includeArchived, CancellationToken cancellationToken) =>
        await db.Assets
            .Where(asset => includeArchived || !asset.Archived)
            .OrderBy(asset => asset.Name)
            .Select(asset => new AssetResponse(asset.Id, asset.Name, asset.Type, asset.AssetGroupId, asset.Archived))
            .ToListAsync(cancellationToken);

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
        await db.SaveChangesAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(asset);
    }

    /// <summary>
    /// Deletes an Asset with no history outright, so mistakes are easy to clean up. An Asset with
    /// history (Readings) is Archived instead, which keeps the history and hides it from lists.
    /// </summary>
    /// <response code="404">No Asset has this id.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var assets = db.Assets.Where(asset => asset.Id == id);
        int found;
        try
        {
            found = await assets.ExecuteDeleteAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // History tables reference assets without ON DELETE, so the database itself says
            // whether history exists, and no Reading can slip in between a check and the delete.
            found = await assets.ExecuteUpdateAsync(setters => setters.SetProperty(asset => asset.Archived, true), cancellationToken);
        }

        return found == 0 ? NotFound() : NoContent();
    }

    private async Task<bool> IsUnknownAssetGroupAsync(Guid? assetGroupId, CancellationToken cancellationToken) =>
        assetGroupId is { } id && !await db.AssetGroups.AnyAsync(group => group.Id == id, cancellationToken);

    private ActionResult UnknownAssetGroup()
    {
        ModelState.AddModelError(nameof(AssetRequest.AssetGroupId), "No Asset Group has this id.");
        return ValidationProblem(ModelState);
    }

    private static AssetResponse ToResponse(Asset asset) =>
        new(asset.Id, asset.Name, asset.Type, asset.AssetGroupId, asset.Archived);
}
