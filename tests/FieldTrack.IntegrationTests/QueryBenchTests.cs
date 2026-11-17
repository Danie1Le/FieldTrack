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

    private BenchTarget Target => new(
        Fixture.BrokerHost, Fixture.BrokerPort, new BenchDatabase(_dataSource), Path.Combine(_directory, "state.json"), Path.Combine(_directory, "results"), "no-such-compose-project");

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

        var outcome = await GeofenceBench.RunAsync(Target, fraction: 0.01, warmRuns: 10, TextWriter.Null, CancellationToken);

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("readings").GetInt64().ShouldBe(Readings);
        results.GetProperty("inside").GetInt64().ShouldBeInRange(1, 20);
        results.GetProperty("fractionInside").GetDouble().ShouldBeInRange(0.001, 0.025);
        results.GetProperty("withIndex").GetProperty("warmMs").GetArrayLength().ShouldBe(10);
        results.GetProperty("withIndex").GetProperty("cold").GetProperty("executionMs").GetDouble().ShouldBeGreaterThan(0);
        var withoutIndex = results.GetProperty("withoutIndex").GetProperty("cold").GetProperty("nodeTypes").EnumerateArray().Select(node => node.GetString()).ToList();
        withoutIndex.ShouldContain("Seq Scan");
        withoutIndex.ShouldNotContain(node => node!.Contains("Index", StringComparison.Ordinal), "the run without the GiST index must not use one");
        outcome.Pass.ShouldBeTrue("a few hundred Readings are far inside the 50 ms bar");
    }

    [Fact]
    public async Task The_history_bench_reads_one_Assets_24_hours_from_both_tables_and_the_partitioned_plan_prunes_to_the_days_it_spans()
    {
        await SeedHistoryAsync();

        var outcome = await HistoryBench.RunAsync(Target, warmRuns: 10, TextWriter.Null, CancellationToken);

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("rows").GetInt64().ShouldBe(96, "24 hours of one Reading per 15 minutes");
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

    public void Dispose()
    {
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
