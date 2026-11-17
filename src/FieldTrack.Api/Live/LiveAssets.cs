using FieldTrack.Api.Assets;
using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Assets;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Live;

/// <summary>
/// Reads Assets as the dashboard shows them, one query for both the REST read and the live push, so a dashboard that
/// reloads after a lost push sees exactly what the push would have sent.
/// </summary>
public static class LiveAssets
{
    // How far back the latest Reading is looked for first: a day or two of daily partitions, not all of them.
    private static readonly TimeSpan _recentReadings = TimeSpan.FromDays(1);

    /// <summary>
    /// The Assets not Archived, by name: all of them when <paramref name="ids"/> is null, else only those listed.
    /// <paramref name="now"/> decides which are Offline.
    /// </summary>
    public static async Task<List<LiveAssetResponse>> ListAsync(
        FieldTrackDbContext db, IReadOnlyCollection<Guid>? ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var all = ids is null;
        Guid[] only = [.. ids ?? []];
        var resolved = nameof(AlertStatus.Resolved);
        var recentFrom = now - _recentReadings;
        // Readings are outside the EF model (ADR 0003). The latest one per Asset comes from the index on
        // (asset_id, device_time): first among the recent partitions only, and only for an Asset with no recent
        // Reading among all of them (COALESCE evaluates its second subquery only when the first is null). Exact
        // either way: a recent Reading is always later than any older one. OFFSET 0 keeps the planner from copying
        // the subqueries into every column of r.
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT a.id, a.name, a.type, a.asset_group_id, t.attached_at, d.last_received_at,
                   EXISTS (SELECT FROM alerts al WHERE al.asset_id = a.id AND al.status <> {resolved}) AS has_active_alert,
                   rd.serial_number AS device_serial_number, r.boot, r.seq AS sequence_number, r.device_time, r.received_time,
                   ST_Y(r.location::geometry) AS latitude, ST_X(r.location::geometry) AS longitude,
                   r.speed_kph, r.engine_temp_c, r.fuel_pct, r.vibration_mm_s, r.battery_v
            FROM assets a
            LEFT JOIN attachments t ON t.asset_id = a.id AND t.detached_at IS NULL
            LEFT JOIN devices d ON d.id = t.device_id
            CROSS JOIN LATERAL (
                SELECT COALESCE(
                    (SELECT x FROM readings x
                     WHERE x.asset_id = a.id AND x.device_time >= {recentFrom}
                     ORDER BY x.device_time DESC, x.device_id DESC, x.boot DESC, x.seq DESC
                     LIMIT 1),
                    (SELECT x FROM readings x
                     WHERE x.asset_id = a.id
                     ORDER BY x.device_time DESC, x.device_id DESC, x.boot DESC, x.seq DESC
                     LIMIT 1)) AS reading
                OFFSET 0) latest
            CROSS JOIN LATERAL (SELECT (latest.reading).*) r
            LEFT JOIN devices rd ON rd.id = r.device_id
            WHERE NOT a.archived AND ({all} OR a.id = ANY({only}))
            ORDER BY a.name, a.id
            """).ToListAsync(cancellationToken);

        return [.. rows.Select(row => new LiveAssetResponse(
            row.Id, row.Name, Enum.Parse<AssetType>(row.Type), row.AssetGroupId,
            AssetStatuses.Of(row.AttachedAt, row.HasActiveAlert, row.LastReceivedAt, now), row.LastReceivedAt,
            row.DeviceSerialNumber is null
                ? null
                : new ReadingResponse(row.DeviceSerialNumber, row.Boot!.Value, row.SequenceNumber!.Value, row.DeviceTime!.Value,
                    row.ReceivedTime!.Value, row.Latitude!.Value, row.Longitude!.Value,
                    row.SpeedKph, row.EngineTempC, row.FuelPct, row.VibrationMmS, row.BatteryV)))];
    }

    // One row of the query above; the Reading columns are all null when the Asset has none.
    private sealed record Row(
        Guid Id, string Name, string Type, Guid? AssetGroupId, DateTimeOffset? AttachedAt, DateTimeOffset? LastReceivedAt, bool HasActiveAlert,
        string? DeviceSerialNumber, int? Boot, long? SequenceNumber, DateTimeOffset? DeviceTime, DateTimeOffset? ReceivedTime,
        double? Latitude, double? Longitude, float? SpeedKph, float? EngineTempC, float? FuelPct, float? VibrationMmS, float? BatteryV);
}
