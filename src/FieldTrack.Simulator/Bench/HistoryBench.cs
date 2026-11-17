using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The history query benchmark (spec #21, #46): one Asset's 24-hour route, read the way <c>GET /assets/{id}/readings</c> reads it, from the
/// partitioned <c>readings</c> and from <see cref="HistorySeeder.UnpartitionedTable"/> with the same indexes. Reports the difference and has no
/// pass bar beyond both reading the same Readings.
/// <para>
/// The window starts at the middle of the Asset's history, so it crosses a UTC day boundary and a partitioned plan has to pick two partitions.
/// </para>
/// </summary>
internal static class HistoryBench
{
    /// <summary>What a table holds of the history window.</summary>
    /// <param name="Count">Readings in the window.</param>
    /// <param name="Md5">Checksum over their keys and Device times in query order.</param>
    internal sealed record RowDigest(long Count, string Md5);

    // The endpoint reads one more than its cap of 100,000 (ReadingsController.MaxReadings) to detect an oversized range; the simulator does not reference the API.
    private const int EndpointLimit = 100_001;

    private static string Query(string table) => $"""
        SELECT d.serial_number AS device_serial_number, r.boot, r.seq AS sequence_number, r.device_time, r.received_time,
               ST_Y(r.location::geometry) AS latitude, ST_X(r.location::geometry) AS longitude,
               r.speed_kph, r.engine_temp_c, r.fuel_pct, r.vibration_mm_s, r.battery_v
        FROM {table} r
        JOIN devices d ON d.id = r.device_id
        WHERE r.asset_id = @asset AND r.device_time >= @from AND r.device_time < @to
        ORDER BY r.device_time, r.device_id, r.boot, r.seq
        LIMIT {EndpointLimit}
        """;

    /// <summary>Times one Asset's 24 hours on both tables and writes the result file. <paramref name="restartPostgres"/> runs before each table's first run, so that run is cold.</summary>
    /// <exception cref="SimulatorException">The database holds no Readings.</exception>
    public static async Task<BenchOutcome> RunAsync(
        BenchTarget target, int warmRuns, Func<CancellationToken, Task> restartPostgres, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var dataSource = target.Database.DataSource;
        var (asset, from) = await ChooseAsync(dataSource, cancellationToken);
        var to = from.AddHours(24);
        var parameters = new Dictionary<string, object> { ["asset"] = asset, ["from"] = from, ["to"] = to };

        // Outside the timed runs: the restart before each arm empties the buffers this read fills.
        var partitionedRows = await RowsAsync(dataSource, "readings", asset, from, to, cancellationToken);
        var unpartitionedRows = await RowsAsync(dataSource, HistorySeeder.UnpartitionedTable, asset, from, to, cancellationToken);

        await log.WriteLineAsync($"Asset {asset}, {from:u} to {to:u}; timing the partitioned table");
        var partitioned = await QueryBench.MeasureAsync(dataSource, Query("readings"), parameters, withoutIndexScans: false, warmRuns, restartPostgres, cancellationToken);
        await log.WriteLineAsync("timing the unpartitioned copy");
        var unpartitioned = await QueryBench.MeasureAsync(dataSource, Query(HistorySeeder.UnpartitionedTable), parameters, withoutIndexScans: false, warmRuns, restartPostgres, cancellationToken);

        var rows = partitioned.Cold.RowsReturned;
        return await target.WriteResultAsync(
            "history",
            started,
            new { asset, from, to, warmRuns, tables = new[] { "readings", HistorySeeder.UnpartitionedTable }, coldBasis = QueryBench.ColdBasis },
            new
            {
                rows,
                // The same rows, not just as many: md5 of each table's Reading keys and Device times in the window.
                sameRows = partitionedRows == unpartitionedRows,
                partitionedRows,
                unpartitionedRows,
                partitioned,
                unpartitioned,
                // Positive: the partitioned table's warm median is that much shorter than the unpartitioned one's; negative: longer.
                partitionedFasterPercent = (unpartitioned.WarmMedianMs - partitioned.WarmMedianMs) / unpartitioned.WarmMedianMs * 100,
                partitionedFasterPercentCaveat = $"Measured on {rows} rows (one Reading per seeded interval), where the difference is mostly planning time; it does not predict an Asset that reports every second (about 86,000 rows in 24 h), which this run did not measure.",
            },
            pass: rows > 0 && rows == unpartitioned.Cold.RowsReturned && partitionedRows == unpartitionedRows,
            cancellationToken);
    }

    // How many Readings of the Asset the window holds, and a checksum over their keys and Device times in query order.
    private static async Task<RowDigest> RowsAsync(
        NpgsqlDataSource dataSource, string table, Guid asset, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT count(*), md5(string_agg(device_id::text || '/' || boot || '/' || seq || '/' || device_time::text, ',' ORDER BY device_time, device_id, boot, seq))
            FROM {table}
            WHERE asset_id = @asset AND device_time >= @from AND device_time < @to
            """);
        command.CommandTimeout = BenchDatabase.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("asset", asset);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new RowDigest(reader.GetInt64(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
    }

    // An Asset that has Readings, and the middle of its history.
    private static async Task<(Guid Asset, DateTimeOffset From)> ChooseAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT a.asset_id, a.first, a.last
            FROM (SELECT asset_id FROM readings WHERE asset_id IS NOT NULL LIMIT 1) one
            CROSS JOIN LATERAL (SELECT one.asset_id, min(device_time) AS first, max(device_time) AS last FROM readings WHERE asset_id = one.asset_id) a
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new SimulatorException("The database holds no Readings of any Asset. Run bench seed-history first.");
        }

        var (first, last) = (reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
        return (reader.GetGuid(0), first + ((last - first) / 2));
    }
}
