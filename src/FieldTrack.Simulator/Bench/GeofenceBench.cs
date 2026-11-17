using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The geofence query benchmark (spec #21, #46): count the Readings inside one Geofence that covers about 1% of them, with the GiST index on
/// <c>readings.location</c> and without index scans. Passes when the warm median with the index is under <see cref="TargetMs"/>.
/// <para>
/// The area is a circle (a 65-vertex polygon, valid as a Geofence) around the first stored Reading, as wide as it takes to hold the
/// requested fraction of Readings, so the 1% comes from the data and not from a guess at where the routes run.
/// </para>
/// </summary>
internal static class GeofenceBench
{
    /// <summary>The bar: warm median milliseconds with the index.</summary>
    public const double TargetMs = 50;

    // PostGIS counts a Reading on an edge as inside (ST_Covers), as the evaluator's Covers does (#32).
    private const string Query = "SELECT count(*) FROM readings WHERE ST_Covers(ST_GeogFromText(@area), location)";

    /// <summary>Measures a Geofence holding about <paramref name="fraction"/> of the Readings and writes the result file.</summary>
    /// <exception cref="SimulatorException">The database holds no Readings.</exception>
    public static async Task<BenchOutcome> RunAsync(BenchTarget target, double fraction, int warmRuns, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var dataSource = target.Database.DataSource;
        var readings = await target.Database.CountAllReadingsAsync(cancellationToken);
        if (readings == 0)
        {
            throw new SimulatorException("The database holds no Readings. Run bench seed-history first.");
        }

        var (centre, radiusMeters, area) = await DefineAreaAsync(dataSource, fraction, cancellationToken);
        var parameters = new Dictionary<string, object> { ["area"] = area };
        await using var count = dataSource.CreateCommand(Query);
        count.CommandTimeout = 600;
        count.Parameters.AddWithValue("area", area);
        var inside = (long)(await count.ExecuteScalarAsync(cancellationToken))!;

        await log.WriteLineAsync($"{inside} of {readings} Readings inside a {radiusMeters:0} m circle; timing with the index");
        var withIndex = await QueryBench.MeasureAsync(dataSource, Query, parameters, withoutIndexScans: false, warmRuns, cancellationToken);
        await log.WriteLineAsync("timing without index scans");
        var withoutIndex = await QueryBench.MeasureAsync(dataSource, Query, parameters, withoutIndexScans: true, warmRuns, cancellationToken);

        return await target.WriteResultAsync(
            "geofence",
            started,
            new { targetFraction = fraction, warmRuns, targetMs = TargetMs, query = Query, centre, radiusMeters, coldBasis = QueryBench.ColdBasis },
            new
            {
                readings,
                inside,
                fractionInside = (double)inside / readings,
                withIndex,
                withoutIndex,
                // How many times faster the index makes the warm median.
                indexSpeedup = withoutIndex.WarmMedianMs / withIndex.WarmMedianMs,
            },
            pass: withIndex.WarmMedianMs < TargetMs,
            cancellationToken);
    }

    // The distance from one Reading within which the requested fraction of all Readings lie, as a circle around it.
    private static async Task<(string Centre, double RadiusMeters, string Area)> DefineAreaAsync(NpgsqlDataSource dataSource, double fraction, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH centre AS (SELECT location AS point FROM readings LIMIT 1),
                 radius AS (
                     SELECT percentile_disc(@fraction) WITHIN GROUP (ORDER BY ST_Distance(r.location, c.point, false)) AS meters
                     FROM readings r, centre c)
            SELECT ST_AsText(c.point::geometry), radius.meters, ST_AsText(ST_Buffer(c.point, radius.meters, 16)::geometry)
            FROM centre c, radius
            """);
        command.CommandTimeout = 600;
        command.Parameters.AddWithValue("fraction", fraction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetDouble(1), reader.GetString(2));
    }
}
