using System.Globalization;
using System.Text;
using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// <c>bench seed-history</c> (spec #21, #46): COPYs a synthetic history of Readings straight into Postgres, bypassing MQTT, for the query
/// benchmarks. Every Device of the fleet drives along a GPX route and reports at a fixed interval over the span (ending at <c>to</c>),
/// in Device-time order across Devices like live ingest. Beside the partitioned <c>readings</c> it builds
/// <see cref="UnpartitionedTable"/>, a copy with the same indexes, for the history comparison.
/// <para>
/// The history is not live-path data: it carries no rows in <c>reading_keys</c> and ignores Attachment periods, and ingest refuses
/// Readings this old (<c>TelemetryPayload.MaxLate</c>), so nothing else could have produced it.
/// </para>
/// </summary>
internal static class HistorySeeder
{
    /// <summary>The unpartitioned copy of <c>readings</c> that the history benchmark compares against.</summary>
    public const string UnpartitionedTable = "readings_unpartitioned";

    // Fixed so two seedings of one fleet produce the same history.
    private const int RandomSeed = 46;
    private const int Boot = 1;
    private const int ProgressEvery = 100_000;

    private const string CopyStatement = """
        COPY readings (device_id, boot, seq, asset_id, device_time, received_time, location, speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v)
        FROM STDIN
        """;

    /// <summary>Seeds the history of <paramref name="fleet"/> and writes the result file.</summary>
    /// <param name="fleet">The Devices; they must exist and be attached to Assets already (<c>seed</c>).</param>
    /// <param name="routes">The routes the Devices drive, handed out in turn.</param>
    /// <param name="target">The platform whose database is seeded.</param>
    /// <param name="to">Where the history ends (exclusive).</param>
    /// <param name="span">How far back it starts.</param>
    /// <param name="interval">Time between a Device's Readings.</param>
    /// <param name="log">Progress.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <exception cref="SimulatorException">The database already holds Readings, or a Device of the fleet is not attached to an Asset.</exception>
    public static async Task<BenchOutcome> SeedAsync(
        FleetFile fleet, IReadOnlyList<GpxRoute> routes, BenchTarget target, DateTimeOffset to, TimeSpan span, TimeSpan interval, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var from = to - span;
        var dataSource = target.Database.DataSource;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (await ScalarAsync<bool>(connection, "SELECT EXISTS (SELECT 1 FROM readings)", cancellationToken))
        {
            throw new SimulatorException("The readings table is not empty. The query benchmarks measure the seeded history alone: start from an empty database (docker compose down -v).");
        }

        var owners = await OwnersAsync(connection, fleet, cancellationToken);
        var (firstDay, lastDay) = (DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime((to - TimeSpan.FromTicks(1)).UtcDateTime));
        await CreatePartitionsAsync(connection, firstDay, lastDay, cancellationToken);

        var random = new Random(RandomSeed);
        // Staggered starts keep Device times apart, as live Devices are, without changing how many Readings each has.
        var tracks = fleet.Devices.Select((device, index) => HistoryTrack.Generate(
            routes[index % routes.Count], device.SpeedKph, random.NextDouble() * routes[index % routes.Count].LengthMeters,
            from + (interval * index / fleet.Devices.Count), to, interval, random).GetEnumerator()).ToList();

        await log.WriteLineAsync($"copying Readings of {fleet.Devices.Count} Devices from {from:u} to {to:u}");
        var readings = await CopyAsync(connection, tracks, owners, log, cancellationToken);

        await log.WriteLineAsync("rebuilding the indexes, then copying into the unpartitioned table");
        // Built in bulk, the way the copy's indexes are, so neither table carries the other's bloat.
        await ExecuteAsync(connection, "REINDEX TABLE readings", cancellationToken);
        await ExecuteAsync(connection, $"""
            DROP TABLE IF EXISTS {UnpartitionedTable};
            CREATE TABLE {UnpartitionedTable} (LIKE readings);
            INSERT INTO {UnpartitionedTable} SELECT * FROM readings;
            CREATE INDEX ix_{UnpartitionedTable}_asset_id_device_time ON {UnpartitionedTable} (asset_id, device_time);
            CREATE INDEX ix_{UnpartitionedTable}_device_id_device_time ON {UnpartitionedTable} (device_id, device_time);
            CREATE INDEX ix_{UnpartitionedTable}_location ON {UnpartitionedTable} USING gist (location);
            """, cancellationToken);
        // Fresh statistics and a visibility map on both, so the planner chooses on real numbers; autovacuum never analyses a partitioned parent.
        await ExecuteAsync(connection, "VACUUM (ANALYZE) readings", cancellationToken);
        await ExecuteAsync(connection, $"VACUUM (ANALYZE) {UnpartitionedTable}", cancellationToken);

        var partitionBytes = await ScalarAsync<long>(connection, "SELECT sum(pg_total_relation_size(relid))::bigint FROM pg_partition_tree('readings') WHERE isleaf", cancellationToken);
        var copyBytes = await ScalarAsync<long>(connection, $"SELECT pg_total_relation_size('{UnpartitionedTable}')", cancellationToken);
        return await target.WriteResultAsync(
            "seed-history",
            started,
            new { assets = fleet.Devices.Count, from, to, intervalSeconds = interval.TotalSeconds, routes = routes.Count, randomSeed = RandomSeed },
            new
            {
                readings,
                readingsPerAsset = readings / fleet.Devices.Count,
                partitions = lastDay.DayNumber - firstDay.DayNumber + 1,
                seconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
                partitionedBytes = partitionBytes,
                unpartitionedBytes = copyBytes,
            },
            pass: true,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<(Guid DeviceId, Guid AssetId)>> OwnersAsync(NpgsqlConnection connection, FleetFile fleet, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT d.serial_number, d.id, a.asset_id
            FROM devices d JOIN attachments a ON a.device_id = d.id AND a.detached_at IS NULL
            WHERE d.serial_number = ANY(@serials)
            """, connection);
        command.Parameters.AddWithValue("serials", fleet.Devices.Select(device => device.Serial).ToArray());
        var bySerial = new Dictionary<string, (Guid, Guid)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bySerial[reader.GetString(0)] = (reader.GetGuid(1), reader.GetGuid(2));
        }

        return [.. fleet.Devices.Select(device => bySerial.TryGetValue(device.Serial, out var owner)
            ? owner
            : throw new SimulatorException($"Device {device.Serial} is not attached to an Asset. Seed the fleet first (the bench command does this itself)."))];
    }

    // The same names and bounds as DatabaseMaintenanceJob, under its advisory lock, so a run beside the API's own job cannot race it.
    private static Task CreatePartitionsAsync(NpgsqlConnection connection, DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder("SELECT pg_advisory_xact_lock(hashtext('readings partitions'));");
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"CREATE TABLE IF NOT EXISTS readings_{day:yyyyMMdd} PARTITION OF readings FOR VALUES FROM ('{day:yyyy-MM-dd}T00:00:00Z') TO ('{day.AddDays(1):yyyy-MM-dd}T00:00:00Z');");
        }

        return ExecuteAsync(connection, sql.ToString(), cancellationToken);
    }

    // Text COPY, because the geography column takes EWKT and the data source has no PostGIS type mapping.
    private static async Task<long> CopyAsync(
        NpgsqlConnection connection, List<IEnumerator<HistoryReading>> tracks, IReadOnlyList<(Guid DeviceId, Guid AssetId)> owners, TextWriter log, CancellationToken cancellationToken)
    {
        long copied = 0;
        await using (var writer = await connection.BeginTextImportAsync(CopyStatement, cancellationToken))
        {
            for (var any = true; any;)
            {
                any = false;
                for (var index = 0; index < tracks.Count; index++)
                {
                    if (!tracks[index].MoveNext())
                    {
                        continue;
                    }

                    any = true;
                    await writer.WriteLineAsync(Line(owners[index], tracks[index].Current));
                    if (++copied % ProgressEvery == 0)
                    {
                        await log.WriteLineAsync($"  {copied} Readings");
                    }
                }
            }
        }

        return copied;
    }

    private static string Line((Guid DeviceId, Guid AssetId) owner, HistoryReading reading) => string.Create(
        CultureInfo.InvariantCulture,
        $"{owner.DeviceId}\t{Boot}\t{reading.Sequence}\t{owner.AssetId}\t{reading.DeviceTime:O}\t{reading.DeviceTime.AddSeconds(1):O}\tSRID=4326;POINT({reading.Position.Lon:R} {reading.Position.Lat:R})\t{reading.SpeedKph:R}\t{reading.EngineTempC:R}\t{reading.FuelPct:R}\t{reading.VibrationMmS:R}\t{reading.BatteryV:R}");

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = BenchDatabase.MaintenanceTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = BenchDatabase.MaintenanceTimeoutSeconds };
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
