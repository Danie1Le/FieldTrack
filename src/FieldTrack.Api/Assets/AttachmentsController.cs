using FieldTrack.Api.Auth;
using FieldTrack.Domain.Assets;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Assets;

/// <summary>
/// Which Device is fitted to which Asset, and since when. Attachments are kept after they end, so a
/// Device can move between Assets without rewriting history. Everyone signed in reads them; only
/// Admins attach and detach.
/// </summary>
[ApiController]
[Route("attachments")]
[Authorize(Policy = Policies.Read)]
public sealed class AttachmentsController(FieldTrackDbContext db, TimeProvider time) : ControllerBase
{
    /// <summary>Lists Attachments newest first, optionally only one Asset's or one Device's history.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<AttachmentResponse>> List(Guid? assetId, Guid? deviceId, CancellationToken cancellationToken) =>
        await db.Attachments
            .Where(attachment => assetId == null || attachment.AssetId == assetId)
            .Where(attachment => deviceId == null || attachment.DeviceId == deviceId)
            .OrderByDescending(attachment => attachment.AttachedAt)
            .Select(attachment => ToResponse(attachment))
            .ToListAsync(cancellationToken);

    /// <summary>Attaches a Device to an Asset from now on.</summary>
    /// <response code="400">A missing or unknown Device or Asset.</response>
    /// <response code="409">The Device is already on an Asset, the Asset already has a Device, or either is Archived.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AttachmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AttachmentResponse>> Attach(AttachmentRequest request, CancellationToken cancellationToken)
    {
        var (deviceId, assetId) = (request.DeviceId!.Value, request.AssetId!.Value);
        // Lock the Device, then the Asset, so concurrent attaches of either queue up and see each
        // other's Attachment, and a concurrent delete or Archive of either finishes first. The partial
        // unique indexes on open Attachments back this up.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var device = await db.Devices.FromSql($"SELECT * FROM devices WHERE id = {deviceId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        var asset = await db.Assets.FromSql($"SELECT * FROM assets WHERE id = {assetId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            ModelState.AddModelError(nameof(AttachmentRequest.DeviceId), "No Device has this id.");
        }

        if (asset is null)
        {
            ModelState.AddModelError(nameof(AttachmentRequest.AssetId), "No Asset has this id.");
        }

        if (device is null || asset is null)
        {
            return ValidationProblem(ModelState);
        }

        if (await RefusalAsync(device, asset, cancellationToken) is { } refusal)
        {
            return Problem(refusal, statusCode: StatusCodes.Status409Conflict);
        }

        var attachment = new Attachment { DeviceId = deviceId, AssetId = assetId, AttachedAt = time.GetUtcNow() };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(attachment));
    }

    /// <summary>Detaches the Device, ending the Attachment now; the Attachment stays in history.</summary>
    /// <response code="404">No Attachment has this id.</response>
    /// <response code="409">The Attachment has already ended.</response>
    [HttpPost("{id:guid}/detach")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AttachmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AttachmentResponse>> Detach(Guid id, CancellationToken cancellationToken)
    {
        // A conditional update, so a racing detach or Archive ends the Attachment only once.
        var ended = await db.Attachments
            .Where(attachment => attachment.Id == id && attachment.DetachedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(attachment => attachment.DetachedAt, time.GetUtcNow()), cancellationToken);
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(attachment => attachment.Id == id, cancellationToken);
        if (attachment is null)
        {
            return NotFound();
        }

        return ended == 0
            ? Problem("The Attachment has already ended.", statusCode: StatusCodes.Status409Conflict)
            : ToResponse(attachment);
    }

    /// <summary>Why the Device cannot be attached to the Asset, or null when it can.</summary>
    private async Task<string?> RefusalAsync(Device device, Asset asset, CancellationToken cancellationToken)
    {
        if (device.Archived)
        {
            return "The Device is Archived.";
        }

        if (asset.Archived)
        {
            return "The Asset is Archived.";
        }

        if (await db.Attachments.AnyAsync(attachment => attachment.DeviceId == device.Id && attachment.DetachedAt == null, cancellationToken))
        {
            return "The Device is already attached to an Asset; detach it first.";
        }

        if (await db.Attachments.AnyAsync(attachment => attachment.AssetId == asset.Id && attachment.DetachedAt == null, cancellationToken))
        {
            return "The Asset already has a Device attached; detach it first.";
        }

        return null;
    }

    private static AttachmentResponse ToResponse(Attachment attachment) =>
        new(attachment.Id, attachment.DeviceId, attachment.AssetId, attachment.AttachedAt, attachment.DetachedAt);
}
