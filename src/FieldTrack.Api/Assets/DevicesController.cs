using FieldTrack.Api.Auth;
using FieldTrack.Api.Live;
using FieldTrack.Domain.Assets;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FieldTrack.Api.Assets;

/// <summary>
/// The registered Devices; telemetry from a serial number not registered here is rejected (spec #21).
/// Everyone signed in reads Devices; only Admins change them.
/// </summary>
[ApiController]
[Route("devices")]
[Authorize(Policy = Policies.Read)]
public sealed class DevicesController(FieldTrackDbContext db, IReadingHistory readingHistory, TimeProvider time, LivePush live) : ControllerBase
{
    /// <summary>Lists Devices by serial number, leaving out Archived ones unless <paramref name="includeArchived"/> is true.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<DeviceResponse>> List(bool includeArchived, CancellationToken cancellationToken) =>
        await db.Devices
            .Where(device => includeArchived || !device.Archived)
            .OrderBy(device => device.SerialNumber)
            .Select(device => ToResponse(device))
            .ToListAsync(cancellationToken);

    /// <summary>Registers a Device by serial number.</summary>
    /// <response code="400">A missing serial number, or one that cannot be a telemetry topic level.</response>
    /// <response code="409">A Device with this serial number is already registered, Archived or not.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<DeviceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceResponse>> Create(DeviceRequest request, CancellationToken cancellationToken)
    {
        var device = new Device { SerialNumber = request.SerialNumber };
        db.Devices.Add(device);
        try
        {
            // The unique index decides, so two Admins registering the same serial at once get one Device.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problem($"A Device with serial number {request.SerialNumber} is already registered.", statusCode: StatusCodes.Status409Conflict);
        }

        return StatusCode(StatusCodes.Status201Created, ToResponse(device));
    }

    /// <summary>
    /// Deletes a Device with no Readings outright, with its Attachments, so mistakes are easy to clean
    /// up. A Device with Readings is Archived instead and its open Attachment ended, which keeps its
    /// history, hides it from lists and makes ingest reject its telemetry; its Asset's Active Alerts resolve as Device
    /// detached.
    /// </summary>
    /// <response code="404">No Device has this id.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // One transaction with the Device row locked, as Asset delete does, so a concurrent attach waits for it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var device = await db.Devices.FromSql($"SELECT * FROM devices WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        // The Asset it is attached to, which the delete leaves without a Device; the lock above keeps attaches out.
        var assetIds = await db.Attachments
            .Where(attachment => attachment.DeviceId == id && attachment.DetachedAt == null)
            .Select(attachment => attachment.AssetId)
            .ToListAsync(cancellationToken);
        List<Guid> resolved = [];
        if (await readingHistory.DeviceHasReadingsAsync(id, cancellationToken))
        {
            device.Archived = true;
            await db.BumpAlertRuleVersionAsync(cancellationToken);
            resolved = await db.EndOpenAttachmentsAsync(attachment => attachment.DeviceId == id, time.GetUtcNow(), cancellationToken);
        }
        else
        {
            // No Readings means no history to keep, so its Attachments go with it (#25).
            await db.Attachments.Where(attachment => attachment.DeviceId == id).ExecuteDeleteAsync(cancellationToken);
            db.Devices.Remove(device);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await live.PushAsync(assetIds, [], resolved, cancellationToken);
        return NoContent();
    }

    private static DeviceResponse ToResponse(Device device) =>
        new(device.Id, device.SerialNumber, device.Archived, device.LastReceivedAt);
}
