using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The geofence query benchmark (spec #21, #46): count the Readings inside one Geofence that covers about 1% of them, with the GiST index on
/// <c>readings.location</c> and without index scans. Passes when the warm median with the index is under <see cref="TargetMs"/>.
/// <para>
/// The area is a square (five positions, like a fence drawn around a site) centred on the first stored Reading and as wide as it takes to
/// hold the requested fraction of Readings, so the 1% comes from the data and not from a guess at where the routes run. Exact tests of a
/// Reading against an area cost more the more vertices it has, so the shape is part of the result (<c>vertices</c>).
/// </para>
/// </summary>
internal static class GeofenceBench
{
    /// <summary>The bar: warm median milliseconds with the index.</summary>
    public const double TargetMs = 50;

    // A degree of latitude, and of longitude at the equator, in metres: the square is sized and measured with the same constant, so it only
    // has to be consistent, not exact.
    private const double MetersPerDegree = 111_320;

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

        var (centre, halfSideMeters, area) = await DefineAreaAsync(dataSource, fraction, cancellationToken);
        var parameters = new Dictionary<string, object> { ["area"] = area };
        await using var count = dataSource.CreateCommand(Query);
        count.CommandTimeout = 600;
        count.Parameters.AddWithValue("area", area);
        var inside = (long)(await count.ExecuteScalarAsync(cancellationToken))!;

        await log.WriteLineAsync($"{inside} of {readings} Readings inside a {halfSideMeters * 2:0} m square; timing with the index");
        var withIndex = await QueryBench.MeasureAsync(dataSource, Query, parameters, withoutIndexScans: false, warmRuns, cancellationToken);
        await log.WriteLineAsync("timing without index scans");
        var withoutIndex = await QueryBench.MeasureAsync(dataSource, Query, parameters, withoutIndexScans: true, warmRuns, cancellationToken);

        return await target.WriteResultAsync(
            "geofence",
            started,
            new { targetFraction = fraction, warmRuns, targetMs = TargetMs, query = Query, shape = "square", vertices = 5, centre, halfSideMeters, coldBasis = QueryBench.ColdBasis },
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

    // The half-side within which the requested fraction of all Readings lie, as a square around one Reading (planar on degrees, like a small fence).
    private static async Task<(string Centre, double HalfSideMeters, string Area)> DefineAreaAsync(NpgsqlDataSource dataSource, double fraction, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH centre AS (SELECT ST_X(location::geometry) AS x, ST_Y(location::geometry) AS y FROM readings LIMIT 1),
                 reach AS (
                     SELECT percentile_disc(@fraction) WITHIN GROUP (ORDER BY greatest(
                                abs(ST_Y(r.location::geometry) - c.y) * @meters,
                                abs(ST_X(r.location::geometry) - c.x) * @meters * cos(radians(c.y)))) AS meters
                     FROM readings r, centre c)
            SELECT ST_AsText(ST_MakePoint(c.x, c.y)), reach.meters,
                   ST_AsText(ST_MakeEnvelope(
                       c.x - reach.meters / (@meters * cos(radians(c.y))), c.y - reach.meters / @meters,
                       c.x + reach.meters / (@meters * cos(radians(c.y))), c.y + reach.meters / @meters, 4326))
            FROM centre c, reach
            """);
        command.CommandTimeout = 600;
        command.Parameters.AddWithValue("fraction", fraction);
        command.Parameters.AddWithValue("meters", MetersPerDegree);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetDouble(1), reader.GetString(2));
    }
}
