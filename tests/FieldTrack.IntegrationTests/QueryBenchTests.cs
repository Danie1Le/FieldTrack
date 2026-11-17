using System.Text.Json;
using FieldTrack.Simulator;
using FieldTrack.Simulator.Bench;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class QueryBenchTests(FieldTrackFixture fixture) : IntegrationTest(fixture), IDisposable
{
    // Three days of one Reading per 15 minutes: 288 per Asset, 864 in all, over the four UTC days 7 to 10 January.
    private const int Readings = 864;
    private static readonly DateTimeOffset _to = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    // About 1.1 km due north, then 1.1 km due east, near Portland like the demo.
    private static readonly GpxRoute[] _routes =
    [
        GpxRoute.Parse("""
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>
              <trkpt lat="45.52" lon="-122.68"/><trkpt lat="45.53" lon="-122.68"/><trkpt lat="45.53" lon="-122.67"/>
            </trkseg></trk></gpx>
            """, "l.gpx"),
    ];

    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "QRY-001", "assetName": "Query 1", "assetType": "Truck" },
          { "serial": "QRY-002", "assetName": "Query 2", "assetType": "Truck" },
          { "serial": "QRY-003", "assetName": "Query 3", "assetType": "Truck" }
        ] }
        """, AppContext.BaseDirectory);

    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-querybench-").FullName;
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(fixture.DatabaseConnectionString);

    private readonly NpgsqlDataSource _noIndexDataSource = NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(fixture.DatabaseConnectionString) { Options = "-c enable_indexscan=off -c enable_indexonlyscan=off -c enable_bitmapscan=off" }.ConnectionString);

    private BenchTarget Target => TargetOn(_dataSource);

    private BenchTarget NoIndexTarget => TargetOn(_noIndexDataSource);

    private BenchTarget TargetOn(NpgsqlDataSource dataSource) => new(
        Fixture.BrokerHost, Fixture.BrokerPort, new BenchDatabase(dataSource), Path.Combine(_directory, "state.json"), Path.Combine(_directory, "results"), "no-such-compose-project");

    private async Task<BenchOutcome> SeedHistoryAsync()
    {
        var api = await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(api, Fleet, TextWriter.Null, CancellationToken);
        return await HistorySeeder.SeedAsync(Fleet, _routes, Target, _to, TimeSpan.FromDays(3), TimeSpan.FromMinutes(15), TextWriter.Null, CancellationToken);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    [Fact]
    public async Task Seeding_a_history_stores_every_Reading_once_in_both_the_partitioned_table_and_its_unpartitioned_copy()
    {
        var outcome = await SeedHistoryAsync();

        JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results").GetProperty("readings").GetInt64().ShouldBe(Readings);
        (await CountAsync("SELECT count(*) FROM readings")).ShouldBe(Readings);
        (await CountAsync("SELECT count(*) FROM readings_unpartitioned")).ShouldBe(Readings);
        (await CountAsync("SELECT count(DISTINCT (device_id, boot, seq)) FROM readings")).ShouldBe(Readings);
        (await CountAsync("SELECT count(*) FROM pg_partition_tree('readings') t JOIN pg_class c ON c.oid = t.relid WHERE t.isleaf AND c.relname BETWEEN 'readings_20260107' AND 'readings_20260110'"))
            .ShouldBe(4);
        (await CountAsync("SELECT count(*) FROM readings WHERE device_time < '2026-01-07T12:00:00Z' OR device_time >= '2026-01-10T12:00:00Z'")).ShouldBe(0);
        (await CountAsync("""
            SELECT count(*) FROM readings r
            JOIN attachments a ON a.asset_id = r.asset_id AND a.device_id = r.device_id AND a.detached_at IS NULL
            """)).ShouldBe(Readings, "every Reading carries the Asset its Device is attached to");
    }

    [Fact]
    public async Task Seeding_into_a_database_that_already_holds_Readings_is_refused()
    {
        await SeedHistoryAsync();

        var again = await Should.ThrowAsync<SimulatorException>(SeedHistoryAsync);

        again.Message.ShouldContain("not empty");
    }

    [Fact]
    public async Task The_geofence_bench_counts_the_Readings_in_an_area_of_about_one_percent_with_and_without_index_scans()
    {
        await SeedHistoryAsync();

        var restarts = 0;
        var outcome = await GeofenceBench.RunAsync(
            Target, fraction: 0.01, GeofenceBench.SquareVertices, warmRuns: 10, _ => { restarts++; return Task.CompletedTask; }, TextWriter.Null, CancellationToken);

        restarts.ShouldBe(4, "a restart before each timed arm of both shapes");
        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("readings").GetInt64().ShouldBe(Readings);
        var shapes = results.GetProperty("shapes").EnumerateArray().ToList();
        shapes.Select(shape => shape.GetProperty("vertices").GetInt32()).ShouldBe([5, 65]);
        shapes.Select(shape => shape.GetProperty("headline").GetBoolean()).ShouldBe([true, false], "the bar applies to the square");
        foreach (var shape in shapes)
        {
            shape.GetProperty("inside").GetInt64().ShouldBeInRange(1, 20);
            shape.GetProperty("fractionInside").GetDouble().ShouldBeInRange(0.001, 0.025);
            shape.GetProperty("usedIndex").GetBoolean().ShouldBeTrue();
            shape.GetProperty("withIndex").GetProperty("warmMs").GetArrayLength().ShouldBe(10);
            shape.GetProperty("withIndex").GetProperty("cold").GetProperty("executionMs").GetDouble().ShouldBeGreaterThan(0);
            var withoutIndex = shape.GetProperty("withoutIndex").GetProperty("cold").GetProperty("nodeTypes").EnumerateArray().Select(node => node.GetString()).ToList();
            withoutIndex.ShouldContain("Seq Scan");
            withoutIndex.ShouldNotContain(node => node!.Contains("Index", StringComparison.Ordinal), "the run without the GiST index must not use one");
        }

        outcome.Pass.ShouldBeTrue("a few hundred Readings are far inside the 50 ms bar");
    }

    [Fact]
    public async Task The_geofence_bench_fails_when_the_run_with_the_index_did_not_use_one()
    {
        await SeedHistoryAsync();

        // A database that plans without index scans (like the no-index arm does) makes "with the index" a Seq Scan: the timing would say nothing about the index.
        var outcome = await GeofenceBench.RunAsync(NoIndexTarget, fraction: 0.01, GeofenceBench.SquareVertices, warmRuns: 3, _ => Task.CompletedTask, TextWriter.Null, CancellationToken);

        outcome.Pass.ShouldBeFalse();
        var shape = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results").GetProperty("shapes")[0];
        shape.GetProperty("usedIndex").GetBoolean().ShouldBeFalse();
        shape.GetProperty("failure").GetString()!.ShouldContain("did not use an index");
    }

    [Fact]
    public async Task The_geofence_bench_refuses_a_shape_it_does_not_build()
    {
        await SeedHistoryAsync();

        var refused = await Should.ThrowAsync<SimulatorException>(
            () => GeofenceBench.RunAsync(Target, fraction: 0.01, vertices: 4, warmRuns: 3, _ => Task.CompletedTask, TextWriter.Null, CancellationToken));

        refused.Message.ShouldContain("--vertices");
    }

    [Fact]
    public async Task The_history_bench_reads_one_Assets_24_hours_from_both_tables_and_the_partitioned_plan_prunes_to_the_days_it_spans()
    {
        await SeedHistoryAsync();

        var restarts = 0;
        var outcome = await HistoryBench.RunAsync(Target, warmRuns: 10, _ => { restarts++; return Task.CompletedTask; }, TextWriter.Null, CancellationToken);

        restarts.ShouldBe(2, "a restart before each table's timed runs");
        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("rows").GetInt64().ShouldBe(96, "24 hours of one Reading per 15 minutes");
        results.GetProperty("sameRows").GetBoolean().ShouldBeTrue();
        results.GetProperty("partitionedRows").GetProperty("md5").GetString().ShouldNotBeNullOrEmpty();
        results.GetProperty("partitionedFasterPercentCaveat").GetString()!.ShouldContain("96 rows");
        var partitioned = results.GetProperty("partitioned");
        var unpartitioned = results.GetProperty("unpartitioned");
        partitioned.GetProperty("cold").GetProperty("rowsReturned").GetInt64().ShouldBe(96);
        unpartitioned.GetProperty("cold").GetProperty("rowsReturned").GetInt64().ShouldBe(96);
        partitioned.GetProperty("cold").GetProperty("relationsScanned").GetInt32().ShouldBeInRange(1, 2, "a 24 h window touches at most two daily partitions of the four");
        unpartitioned.GetProperty("cold").GetProperty("relationsScanned").GetInt32().ShouldBe(1);
        partitioned.GetProperty("warmMs").GetArrayLength().ShouldBe(10);
        results.GetProperty("partitionedFasterPercent").ValueKind.ShouldBe(JsonValueKind.Number);
        outcome.Pass.ShouldBeTrue();
    }

    [Fact]
    public async Task The_history_bench_fails_when_the_unpartitioned_copy_holds_other_Readings_in_the_same_number()
    {
        await SeedHistoryAsync();
        await using (var command = _dataSource.CreateCommand("UPDATE readings_unpartitioned SET seq = seq + 100000"))
        {
            await command.ExecuteNonQueryAsync(CancellationToken);
        }

        var outcome = await HistoryBench.RunAsync(Target, warmRuns: 3, _ => Task.CompletedTask, TextWriter.Null, CancellationToken);

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("partitionedRows").GetProperty("count").GetInt64().ShouldBe(results.GetProperty("unpartitionedRows").GetProperty("count").GetInt64());
        results.GetProperty("sameRows").GetBoolean().ShouldBeFalse();
        outcome.Pass.ShouldBeFalse();
    }

    public void Dispose()
    {
        _noIndexDataSource.Dispose();
        // The shared database outlives this class: take back the old partitions and the copy so another test class does not meet them.
        using var command = _dataSource.CreateCommand("""
            DROP TABLE IF EXISTS readings_unpartitioned;
            DROP TABLE IF EXISTS readings_20260107, readings_20260108, readings_20260109, readings_20260110;
            """);
        command.ExecuteNonQuery();
        _dataSource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
