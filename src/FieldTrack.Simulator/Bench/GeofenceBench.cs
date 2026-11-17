using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The geofence query benchmark (spec #21, #46): count the Readings inside one Geofence that covers about 1% of them, with the GiST index on
/// <c>readings.location</c> and without index scans.
/// <para>
/// The bar (<see cref="TargetMs"/>, warm median with the index) applies to the headline shape only: by default a square of five positions,
/// like a fence drawn around a site. Exact tests of a Reading against an area cost more the more vertices it has, so a 65-position circle of
/// the same 1% (<see cref="CircleVertices"/>), closer to what a map editor produces, is measured in the same run and reported without a bar.
/// </para>
/// <para>
/// Each area is centred on the first stored Reading and as wide as it takes to hold the requested fraction of Readings, so the 1% comes
/// from the data and not from a guess at where the routes run. The area is calibrated first; Postgres is then restarted before each timed
/// arm (see <see cref="QueryBench.ColdBasis"/>).
/// </para>
/// </summary>
internal static class GeofenceBench
{
    /// <summary>The bar: warm median milliseconds with the index, for the headline shape.</summary>
    public const double TargetMs = 50;

    /// <summary>Positions of the square, the headline shape: four corners and the repeated first one.</summary>
    public const int SquareVertices = 5;

    /// <summary>Positions of the circle that is always measured next to a square.</summary>
    public const int CircleVertices = 65;

    // The most positions a Geofence may have (GeofencesController.MaxPositions; the simulator does not reference the API).
    private const int MaxVertices = 1000;

    // A degree of latitude, and of longitude at the equator, in metres: the area is sized and measured with the same constant, so it only
    // has to be consistent, not exact.
    private const double MetersPerDegree = 111_320;

    // PostGIS counts a Reading on an edge as inside (ST_Covers), as the evaluator's Covers does (#32).
    private const string Query = "SELECT count(*) FROM readings WHERE ST_Covers(ST_GeogFromText(@area), location)";

    private const string BarAppliesTo =
        "pass is the warm median with the index under targetMs for the headline shape only (the square of five positions unless --vertices says otherwise); other shapes are reported, not gated. Every with-index run must also use an index.";

    /// <summary>
    /// Measures Geofences of <paramref name="vertices"/> positions (and the circle) holding about <paramref name="fraction"/> of the Readings, and writes
    /// the result file. <paramref name="restartPostgres"/> runs before each arm's first run, so that run is cold.
    /// </summary>
    /// <exception cref="SimulatorException">The database holds no Readings, or <paramref name="vertices"/> is not a shape this bench builds.</exception>
    public static async Task<BenchOutcome> RunAsync(
        BenchTarget target, double fraction, int vertices, int warmRuns, Func<CancellationToken, Task> restartPostgres, TextWriter log, CancellationToken cancellationToken)
    {
        if (vertices is < SquareVertices or > MaxVertices)
        {
            throw new SimulatorException($"--vertices must be {SquareVertices} (a square) up to {MaxVertices} (a circle), got {vertices}.");
        }

        var started = DateTimeOffset.UtcNow;
        var dataSource = target.Database.DataSource;
        var readings = await target.Database.CountAllReadingsAsync(cancellationToken);
        if (readings == 0)
        {
            throw new SimulatorException("The database holds no Readings. Run bench seed-history first.");
        }

        var shapes = new List<object>();
        var pass = true;
        int[] measured = vertices == CircleVertices ? [vertices] : [vertices, CircleVertices];
        foreach (var shapeVertices in measured)
        {
            var headline = shapes.Count == 0;
            var shape = await MeasureShapeAsync(dataSource, shapeVertices, headline, fraction, readings, warmRuns, restartPostgres, log, cancellationToken);
            shapes.Add(shape.Result);
            pass &= shape.UsedIndex && (!headline || shape.WarmMedianMs < TargetMs);
        }

        return await target.WriteResultAsync(
            "geofence",
            started,
            new { targetFraction = fraction, warmRuns, targetMs = TargetMs, query = Query, coldBasis = QueryBench.ColdBasis, barAppliesTo = BarAppliesTo },
            new { readings, shapes },
            pass,
            cancellationToken);
    }

    private static async Task<(object Result, bool UsedIndex, double WarmMedianMs)> MeasureShapeAsync(
        NpgsqlDataSource dataSource, int vertices, bool headline, double fraction, long readings, int warmRuns, Func<CancellationToken, Task> restartPostgres, TextWriter log,
        CancellationToken cancellationToken)
    {
        var shape = vertices == SquareVertices ? "square" : "circle";
        var (centre, reachMeters, area) = await DefineAreaAsync(dataSource, shape, vertices, fraction, cancellationToken);
        var parameters = new Dictionary<string, object> { ["area"] = area };

        await log.WriteLineAsync($"{shape} of {vertices} positions, {reachMeters:0} m from the centre; timing with the index");
        var withIndex = await QueryBench.MeasureAsync(dataSource, Query, parameters, withoutIndexScans: false, warmRuns, restartPostgres, cancellationToken);
        await log.WriteLineAsync("timing without index scans");
        var withoutIndex = await QueryBench.MeasureAsync(dataSource, Query, parameters, withoutIndexScans: true, warmRuns, restartPostgres, cancellationToken);

        await using var count = dataSource.CreateCommand(Query);
        count.CommandTimeout = BenchDatabase.CommandTimeoutSeconds;
        count.Parameters.AddWithValue("area", area);
        var inside = (long)(await count.ExecuteScalarAsync(cancellationToken))!;

        // A Seq Scan with the index "on" would make the speedup ~1x and the median a lie about the index.
        var usedIndex = withIndex.Cold.UsedIndex && withIndex.LastWarm.UsedIndex;
        var result = new
        {
            headline,
            shape,
            vertices,
            centre,
            // Half the side of the square, or the radius of the circle.
            reachMeters,
            inside,
            fractionInside = (double)inside / readings,
            usedIndex,
            failure = usedIndex ? null : "the run with the index did not use an index, so the timing says nothing about it",
            withIndex,
            withoutIndex,
            // How many times faster the index makes the warm median.
            indexSpeedup = withoutIndex.WarmMedianMs / withIndex.WarmMedianMs,
        };
        return (result, usedIndex, withIndex.WarmMedianMs);
    }

    // The reach within which the requested fraction of all Readings lie around one Reading (planar on degrees, like a small fence): the largest
    // axis distance for a square, the distance for a circle. The area is that square, or a regular polygon of that radius.
    private static async Task<(string Centre, double ReachMeters, string Area)> DefineAreaAsync(
        NpgsqlDataSource dataSource, string shape, int vertices, double fraction, CancellationToken cancellationToken)
    {
        const string dy = "abs(ST_Y(r.location::geometry) - c.y) * @meters";
        const string dx = "abs(ST_X(r.location::geometry) - c.x) * @meters * cos(radians(c.y))";
        var distance = shape == "square" ? $"greatest({dy}, {dx})" : $"sqrt(power({dy}, 2) + power({dx}, 2))";
        var area = shape == "square"
            ? """
              ST_MakeEnvelope(
                  c.x - reach.meters / (@meters * cos(radians(c.y))), c.y - reach.meters / @meters,
                  c.x + reach.meters / (@meters * cos(radians(c.y))), c.y + reach.meters / @meters, 4326)
              """
            // k = 0 .. n - 1 around the centre, and k = n repeats k = 0 exactly (k % n), which closes the ring.
            : """
              (SELECT ST_MakePolygon(ST_MakeLine(ST_MakePoint(
                          c.x + reach.meters / (@meters * cos(radians(c.y))) * cos(2 * pi() * (k % (@vertices - 1)) / (@vertices - 1)),
                          c.y + reach.meters / @meters * sin(2 * pi() * (k % (@vertices - 1)) / (@vertices - 1))) ORDER BY k))
               FROM generate_series(0, @vertices - 1) k)
              """;
        await using var command = dataSource.CreateCommand($"""
            WITH centre AS (SELECT ST_X(location::geometry) AS x, ST_Y(location::geometry) AS y FROM readings LIMIT 1),
                 reach AS (SELECT percentile_disc(@fraction) WITHIN GROUP (ORDER BY {distance}) AS meters FROM readings r, centre c)
            SELECT ST_AsText(ST_MakePoint(c.x, c.y)), reach.meters, ST_AsText({area})
            FROM centre c, reach
            """);
        command.CommandTimeout = BenchDatabase.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("fraction", fraction);
        command.Parameters.AddWithValue("meters", MetersPerDegree);
        command.Parameters.AddWithValue("vertices", vertices);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetDouble(1), reader.GetString(2));
    }
}
