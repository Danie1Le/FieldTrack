using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>Times a query with <c>EXPLAIN (ANALYZE, BUFFERS)</c> the way the query benchmarks (#46) record it: a first run, then the median of several.</summary>
internal static class QueryBench
{
    /// <summary>How many warm runs follow the first; the median of these is the headline (spec #21).</summary>
    public const int WarmRuns = 10;

    /// <summary>
    /// What the "cold" run is, written into every result: the first execution on this database after the preceding step, not an empty cache.
    /// Shared buffers are cold only if Postgres was restarted first, and the operating system's cache stays warm either way.
    /// </summary>
    public const string ColdBasis = "first execution; shared buffers are cold only if Postgres was restarted beforehand, the OS cache is not flushed";

    /// <summary>
    /// Runs <paramref name="sql"/> once and then <paramref name="warmRuns"/> more times under <c>EXPLAIN (ANALYZE, BUFFERS)</c>. With
    /// <paramref name="withoutIndexScans"/> the planner may not use index scans, index-only scans or bitmap scans (it falls back to a
    /// sequential scan): the way to measure a query without its index without dropping an index that takes minutes to rebuild.
    /// </summary>
    public static async Task<QueryTiming> MeasureAsync(
        NpgsqlDataSource dataSource, string sql, IReadOnlyDictionary<string, object> parameters, bool withoutIndexScans, int warmRuns, CancellationToken cancellationToken)
    {
        var cold = await ExplainAsync(dataSource, sql, parameters, withoutIndexScans, cancellationToken);
        var warm = new List<QueryPlan>(warmRuns);
        for (var run = 0; run < warmRuns; run++)
        {
            warm.Add(await ExplainAsync(dataSource, sql, parameters, withoutIndexScans, cancellationToken));
        }

        return QueryTiming.Of(cold, warm);
    }

    private static async Task<QueryPlan> ExplainAsync(
        NpgsqlDataSource dataSource, string sql, IReadOnlyDictionary<string, object> parameters, bool withoutIndexScans, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // SET LOCAL ends with the transaction, so a pooled connection never carries the planner switches into another query.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (withoutIndexScans)
        {
            await using var set = new NpgsqlCommand("SET LOCAL enable_indexscan = off; SET LOCAL enable_indexonlyscan = off; SET LOCAL enable_bitmapscan = off", connection, transaction);
            await set.ExecuteNonQueryAsync(cancellationToken);
        }

        // A scan of a large table on a database that is busy otherwise outlasts Npgsql's 30 s default.
        await using var command = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {sql}", connection, transaction) { CommandTimeout = 600 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return QueryPlan.Parse((string)(await command.ExecuteScalarAsync(cancellationToken))!);
    }
}
