using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>Ingest latency of the Readings stored in a window: milliseconds from the Device's publish time to the commit of their batch.</summary>
/// <param name="Count">How many Readings.</param>
/// <param name="P50Ms">Median.</param>
/// <param name="P95Ms">95th percentile.</param>
/// <param name="P99Ms">99th percentile.</param>
/// <param name="MaxMs">Slowest.</param>
internal sealed record IngestLatency(long Count, double P50Ms, double P95Ms, double P99Ms, double MaxMs);

/// <summary>What the database holds of a run, set against its <see cref="RunManifest"/>.</summary>
/// <param name="Published">Readings the Manifest lists.</param>
/// <param name="Missing">Listed Readings that are not stored.</param>
/// <param name="Stored">Readings stored in the run's time window, listed or not.</param>
/// <param name="StoredDuplicates">Stored Readings that repeat another stored Reading's key (Device, Boot, Sequence number).</param>
internal sealed record ManifestScore(long Published, long Missing, long Stored, long StoredDuplicates);

/// <summary>
/// The bench's read-only view of the platform's database: it counts Readings, reads their commit times and scores them against a Manifest.
/// A run's window is its Manifest's start to its end (Device time), padded by a second for rounding. It must stay tight: the previous
/// run's Readings sit just before it, and counting them would make a run look drained before it was.
/// </summary>
internal sealed class BenchDatabase(NpgsqlDataSource dataSource)
{
    /// <summary>How far either side of a run Device time may fall and still count as the run's.</summary>
    public static readonly TimeSpan Padding = TimeSpan.FromSeconds(1);

    /// <summary>Readings stored with Device time from <paramref name="from"/> up to, not including, <paramref name="to"/>.</summary>
    public async Task<long> CountReadingsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await ScalarAsync<long>("SELECT count(*) FROM readings WHERE device_time >= @from AND device_time < @to", from, to, cancellationToken);

    /// <summary>How many Rejected messages are stored.</summary>
    public async Task<long> CountRejectedAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM rejected_messages");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Commit time minus Device time of every Reading in the window, in milliseconds, corrected by <paramref name="clockOffset"/>
    /// (database clock minus this machine's). Device time is the publish time of a simulated Device (no clock Faults in a bench).
    /// Null when the window holds no Reading.
    /// </summary>
    /// <exception cref="SimulatorException">Postgres does not record commit times (<c>track_commit_timestamp</c> is off).</exception>
    public async Task<IngestLatency?> LatencyAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan clockOffset, CancellationToken cancellationToken)
    {
        if (await SettingAsync("track_commit_timestamp", cancellationToken) != "on")
        {
            throw new SimulatorException("Postgres has track_commit_timestamp off, so commit times are unknown. Start the stack with bench/docker-compose.bench.yml.");
        }

        await using var command = dataSource.CreateCommand("""
            SELECT count(*), percentile_cont(ARRAY[0.5, 0.95, 0.99]) WITHIN GROUP (ORDER BY lag), max(lag)
            FROM (SELECT extract(epoch FROM pg_xact_commit_timestamp(xmin) - device_time) * 1000 AS lag
                  FROM readings WHERE device_time >= @from AND device_time < @to) t
            """);
        command.Parameters.AddWithValue("from", from - Padding);
        command.Parameters.AddWithValue("to", to + Padding);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetInt64(0) == 0)
        {
            return null;
        }

        var shift = clockOffset.TotalMilliseconds;
        var percentiles = reader.GetFieldValue<double[]>(1);
        return new IngestLatency(reader.GetInt64(0), percentiles[0] - shift, percentiles[1] - shift, percentiles[2] - shift, reader.GetDouble(2) - shift);
    }

    /// <summary>
    /// How far the database's clock runs ahead of this machine's, from the quickest of a few round trips (the NTP estimate: the database's
    /// reading against the midpoint of the request). Latency is measured across the two clocks, so it is corrected by this.
    /// </summary>
    public async Task<TimeSpan> ClockOffsetAsync(CancellationToken cancellationToken)
    {
        var best = (RoundTrip: TimeSpan.MaxValue, Offset: TimeSpan.Zero);
        for (var i = 0; i < 20; i++)
        {
            var before = DateTimeOffset.UtcNow;
            await using var command = dataSource.CreateCommand("SELECT clock_timestamp()");
            var database = (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
            var after = DateTimeOffset.UtcNow;
            if (after - before < best.RoundTrip)
            {
                best = (after - before, new DateTimeOffset(DateTime.SpecifyKind(database, DateTimeKind.Utc)) - (before + ((after - before) / 2)));
            }
        }

        return best.Offset;
    }

    /// <summary>Scores the stored Readings against <paramref name="manifest"/>: which listed Readings are missing, and whether any key is stored twice.</summary>
    public async Task<ManifestScore> ScoreAsync(RunManifest manifest, CancellationToken cancellationToken)
    {
        var ranges = manifest.Devices.SelectMany(device => device.Ranges.Select(range => (device.Serial, range))).ToList();
        await using var command = dataSource.CreateCommand("""
            SELECT coalesce(sum(x.last_seq - x.first_seq + 1), 0)::bigint AS published,
                   coalesce(sum(x.last_seq - x.first_seq + 1 - coalesce(found.n, 0)), 0)::bigint AS missing
            FROM unnest(@serials, @boots, @firsts, @lasts) AS x(serial, boot, first_seq, last_seq)
            LEFT JOIN LATERAL (
                SELECT count(DISTINCT r.seq) AS n
                FROM readings r JOIN devices d ON d.id = r.device_id
                WHERE d.serial_number = x.serial AND r.boot = x.boot AND r.seq BETWEEN x.first_seq AND x.last_seq
                  AND r.device_time >= @from AND r.device_time < @to) found ON true
            """);
        command.Parameters.AddWithValue("serials", ranges.Select(r => r.Serial).ToArray());
        command.Parameters.AddWithValue("boots", ranges.Select(r => r.range.Boot).ToArray());
        command.Parameters.AddWithValue("firsts", ranges.Select(r => r.range.FirstSequence).ToArray());
        command.Parameters.AddWithValue("lasts", ranges.Select(r => r.range.LastSequence).ToArray());
        command.Parameters.AddWithValue("from", manifest.StartedAt - Padding);
        command.Parameters.AddWithValue("to", manifest.EndedAt + Padding);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var (published, missing) = (reader.GetInt64(0), reader.GetInt64(1));
        await reader.CloseAsync();

        var stored = await CountReadingsAsync(manifest.StartedAt - Padding, manifest.EndedAt + Padding, cancellationToken);
        var distinct = await ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT DISTINCT device_id, boot, seq FROM readings WHERE device_time >= @from AND device_time < @to) k",
            manifest.StartedAt - Padding, manifest.EndedAt + Padding, cancellationToken);
        return new ManifestScore(published, missing, stored, stored - distinct);
    }

    /// <summary>The named Postgres settings, as the server reports them.</summary>
    public async Task<IReadOnlyDictionary<string, string>> SettingsAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT name, setting FROM pg_settings WHERE name = ANY(@names)");
        command.Parameters.AddWithValue("names", names.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }

    private async Task<string?> SettingAsync(string name, CancellationToken cancellationToken) =>
        (await SettingsAsync([name], cancellationToken)).GetValueOrDefault(name);

    private async Task<T> ScalarAsync<T>(string sql, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
