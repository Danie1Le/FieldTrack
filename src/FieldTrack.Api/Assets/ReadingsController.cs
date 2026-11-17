using System.ComponentModel.DataAnnotations;
using FieldTrack.Api.Auth;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Assets;

/// <summary>An Asset's stored Readings, for history views and route replay. Everyone signed in reads them.</summary>
[ApiController]
[Route("assets/{assetId:guid}/readings")]
[Authorize(Policy = Policies.Read)]
public sealed class ReadingsController(FieldTrackDbContext db) : ControllerBase
{
    /// <summary>The longest range one request may ask for, the same bound as a CSV export (spec #21).</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    /// <summary>
    /// The most Readings one request returns. This read is materialized in the process that also runs ingest,
    /// so a busy Asset's long range is refused instead of filling memory; the streamed CSV export (#42) has no cap.
    /// </summary>
    public const int MaxReadings = 100_000;

    /// <summary>
    /// Lists the Readings stamped with the Asset whose Device time is in [<paramref name="from"/>, <paramref name="to"/>),
    /// ordered by Device time. Archived Assets keep their history, so they are listed too.
    /// </summary>
    /// <response code="400">A missing bound, <paramref name="to"/> not after <paramref name="from"/>, a range over 31 days, or one holding more than <see cref="MaxReadings"/> Readings.</response>
    /// <response code="404">No Asset has this id.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ReadingResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ReadingResponse>>> List(
        Guid assetId,
        [Required] DateTimeOffset? from,
        [Required] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        // Npgsql writes timestamptz from UTC values only.
        var (start, end) = (from!.Value.ToUniversalTime(), to!.Value.ToUniversalTime());
        if (end <= start || end - start > MaxRange)
        {
            ModelState.AddModelError(nameof(to), $"The range must end after it starts and span at most {MaxRange.Days} days.");
            return ValidationProblem(ModelState);
        }

        if (!await db.Assets.AnyAsync(asset => asset.Id == assetId, cancellationToken))
        {
            return NotFound();
        }

        // Readings are outside the EF model (ADR 0003), so this reads them with SQL; the index on
        // (asset_id, device_time) and partition pruning on device_time serve it.
        var readings = await db.Database.SqlQuery<ReadingResponse>($"""
            SELECT d.serial_number AS device_serial_number, r.boot, r.seq AS sequence_number, r.device_time, r.received_time,
                   ST_Y(r.location::geometry) AS latitude, ST_X(r.location::geometry) AS longitude,
                   r.speed_kph, r.engine_temp_c, r.fuel_pct, r.vibration_mm_s, r.battery_v
            FROM readings r
            JOIN devices d ON d.id = r.device_id
            WHERE r.asset_id = {assetId} AND r.device_time >= {start} AND r.device_time < {end}
            ORDER BY r.device_time, r.device_id, r.boot, r.seq
            LIMIT {MaxReadings + 1}
            """).ToListAsync(cancellationToken);
        if (readings.Count > MaxReadings)
        {
            ModelState.AddModelError(nameof(to), $"The range holds more than {MaxReadings} Readings; ask for a shorter one.");
            return ValidationProblem(ModelState);
        }

        return readings;
    }
}
