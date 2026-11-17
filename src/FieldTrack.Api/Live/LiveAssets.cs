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
        // Readings are outside the EF model (ADR 0003). The latest one per Asset comes from the index on
        // (asset_id, device_time), probed once per partition.
        // shortcut: no lower bound on device_time, so the probes grow with the partition count; bound it to the
        // retention window once partitions are dropped.
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT a.id, a.name, a.type, a.asset_group_id, d.id IS NOT NULL AS has_device, d.last_received_at,
                   EXISTS (SELECT FROM alerts al WHERE al.asset_id = a.id AND al.status <> {resolved}) AS has_active_alert,
                   r.serial_number AS device_serial_number, r.boot, r.seq AS sequence_number, r.device_time, r.received_time,
                   r.latitude, r.longitude, r.speed_kph, r.engine_temp_c, r.fuel_pct, r.vibration_mm_s, r.battery_v
            FROM assets a
            LEFT JOIN attachments t ON t.asset_id = a.id AND t.detached_at IS NULL
            LEFT JOIN devices d ON d.id = t.device_id
            LEFT JOIN LATERAL (
                SELECT rd.serial_number, x.boot, x.seq, x.device_time, x.received_time,
                       ST_Y(x.location::geometry) AS latitude, ST_X(x.location::geometry) AS longitude,
                       x.speed_kph, x.engine_temp_c, x.fuel_pct, x.vibration_mm_s, x.battery_v
                FROM readings x
                JOIN devices rd ON rd.id = x.device_id
                WHERE x.asset_id = a.id
                ORDER BY x.device_time DESC, x.device_id DESC, x.boot DESC, x.seq DESC
                LIMIT 1) r ON true
            WHERE NOT a.archived AND ({all} OR a.id = ANY({only}))
            ORDER BY a.name, a.id
            """).ToListAsync(cancellationToken);

        return [.. rows.Select(row => new LiveAssetResponse(
            row.Id, row.Name, Enum.Parse<AssetType>(row.Type), row.AssetGroupId,
            AssetStatuses.Of(row.HasDevice, row.HasActiveAlert, row.LastReceivedAt, now), row.LastReceivedAt,
            row.DeviceSerialNumber is null
                ? null
                : new ReadingResponse(row.DeviceSerialNumber, row.Boot!.Value, row.SequenceNumber!.Value, row.DeviceTime!.Value,
                    row.ReceivedTime!.Value, row.Latitude!.Value, row.Longitude!.Value,
                    row.SpeedKph, row.EngineTempC, row.FuelPct, row.VibrationMmS, row.BatteryV)))];
    }

    // One row of the query above; the Reading columns are all null when the Asset has none.
    private sealed record Row(
        Guid Id, string Name, string Type, Guid? AssetGroupId, bool HasDevice, DateTimeOffset? LastReceivedAt, bool HasActiveAlert,
        string? DeviceSerialNumber, int? Boot, long? SequenceNumber, DateTimeOffset? DeviceTime, DateTimeOffset? ReceivedTime,
        double? Latitude, double? Longitude, float? SpeedKph, float? EngineTempC, float? FuelPct, float? VibrationMmS, float? BatteryV);
}
