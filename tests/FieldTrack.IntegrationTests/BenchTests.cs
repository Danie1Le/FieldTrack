using System.Text.Json;
using FieldTrack.Simulator;
using FieldTrack.Simulator.Bench;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class BenchTests(FieldTrackFixture fixture) : IntegrationTest(fixture), IDisposable
{
    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "BNC-001", "assetName": "Bench 1", "assetType": "Truck", "sensors": ["speedKph", "engineTempC"] },
          { "serial": "BNC-002", "assetName": "Bench 2", "assetType": "Truck", "sensors": ["speedKph", "fuelPct"] },
          { "serial": "BNC-003", "assetName": "Bench 3", "assetType": "Truck" }
        ] }
        """, AppContext.BaseDirectory);

    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-bench-").FullName;
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(fixture.DatabaseConnectionString);

    private BenchTarget Target => new(
        Fixture.BrokerHost, Fixture.BrokerPort, new BenchDatabase(_dataSource), Path.Combine(_directory, "state.json"), Path.Combine(_directory, "results"), "no-such-compose-project");

    private async Task SeedAsync()
    {
        var api = await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(api, Fleet, TextWriter.Null, CancellationToken);
    }

    [Fact]
    public async Task A_sustained_run_stores_everything_it_published_and_reports_latency_backlog_and_environment()
    {
        await SeedAsync();
        await using var ingest = await StartIngestAsync();

        var outcome = await ThroughputBench.SustainedAsync(Fleet, Target, rate: 60, TimeSpan.FromSeconds(8), TextWriter.Null, CancellationToken);

        File.Exists(outcome.Path).ShouldBeTrue();
        var result = JsonDocument.Parse(outcome.Json).RootElement;
        var results = result.GetProperty("results");
        results.GetProperty("published").GetInt64().ShouldBeInRange(300, 600);
        results.GetProperty("stored").GetInt64().ShouldBe(results.GetProperty("published").GetInt64());
        results.GetProperty("missing").GetInt64().ShouldBe(0);
        results.GetProperty("storedDuplicates").GetInt64().ShouldBe(0);
        var latency = results.GetProperty("ingestLatencyMs");
        latency.GetProperty("count").GetInt64().ShouldBe(results.GetProperty("published").GetInt64());
        latency.GetProperty("p95Ms").GetDouble().ShouldBeInRange(0, 5_000, "commit follows publish, within a few seconds on any machine");
        var puback = results.GetProperty("tickToPubackMs");
        puback.GetProperty("count").GetInt64().ShouldBe(results.GetProperty("published").GetInt64());
        puback.GetProperty("p95Ms").GetDouble().ShouldBeInRange(0, 5_000);
        results.GetProperty("latencyBasis").GetString()!.ShouldContain("lower bound");
        results.GetProperty("achievedRatePerSecond").GetDouble().ShouldBeInRange(30, 70);
        results.GetProperty("rateAchieved").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        var offsets = results.GetProperty("clockOffsetMs");
        offsets.GetProperty("before").GetDouble().ShouldBeInRange(-1_000, 1_000);
        offsets.GetProperty("after").GetDouble().ShouldBeInRange(-1_000, 1_000);
        results.GetProperty("backlog").GetProperty("samples").GetInt32().ShouldBeGreaterThan(3);
        var environment = result.GetProperty("environment");
        environment.GetProperty("postgres").GetProperty("track_commit_timestamp").GetString().ShouldBe("on");
        environment.GetProperty("postgres").GetProperty("max_wal_size").GetString().ShouldNotBeNullOrEmpty();
        environment.GetProperty("postgres").GetProperty("shared_buffers").GetString().ShouldEndWith("MB", Case.Insensitive, "with its unit, not 8 kB blocks");
        environment.GetProperty("os").GetString().ShouldNotBeNullOrEmpty();
        environment.GetProperty("cpu").GetProperty("logicalCores").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_flood_run_reports_the_rate_it_published_and_ingest_absorbed_and_loses_nothing()
    {
        await SeedAsync();
        await using var ingest = await StartIngestAsync();

        var outcome = await ThroughputBench.FloodAsync(Fleet, Target, TimeSpan.FromSeconds(4), TextWriter.Null, CancellationToken);

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("publishedPerSecond").GetDouble().ShouldBeGreaterThan(10);
        results.GetProperty("ingestedPerSecond").GetDouble().ShouldBeGreaterThan(10);
        results.GetProperty("missing").GetInt64().ShouldBe(0);
        outcome.Pass.ShouldBeTrue();
    }

    [Fact]
    public async Task Restarting_ingest_during_a_run_loses_nothing_and_stores_nothing_twice()
    {
        await SeedAsync();
        var clientId = NewIngestClientId();
        var ingest = await StartIngestAsync(clientId: clientId);

        // A new worker on the same broker session takes over what the old one had not acknowledged. The compose bench SIGKILLs a container instead.
        async Task RestartAsync(CancellationToken stop)
        {
            await ingest.DisposeAsync();
            ingest = await StartIngestAsync(clientId: clientId);
        }

        var outcome = await CrashBench.RunAsync(
            Fleet, Target, rate: 60, TimeSpan.FromSeconds(9), [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], RestartAsync, TextWriter.Null, CancellationToken);
        await ingest.DisposeAsync();

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("kills").GetArrayLength().ShouldBe(2);
        results.GetProperty("published").GetInt64().ShouldBeGreaterThan(300);
        results.GetProperty("missing").GetInt64().ShouldBe(0);
        results.GetProperty("storedDuplicates").GetInt64().ShouldBe(0);
        results.GetProperty("copiesDroppedByIngest").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        outcome.Pass.ShouldBeTrue();
    }

    [Fact]
    public async Task A_database_clock_ahead_of_ours_lowers_the_latency_by_that_much()
    {
        await SeedAsync();
        await using var ingest = await StartIngestAsync();
        var run = await BenchRun.RunAsync(Fleet, Target, "offset", rate: 30, TimeSpan.FromSeconds(3), during: null, TextWriter.Null, CancellationToken);

        var plain = await Target.Database.LatencyAsync(run.Manifest.StartedAt, run.Manifest.EndedAt, TimeSpan.Zero, CancellationToken);
        var corrected = await Target.Database.LatencyAsync(run.Manifest.StartedAt, run.Manifest.EndedAt, TimeSpan.FromSeconds(10), CancellationToken);

        (plain!.P95Ms - corrected!.P95Ms).ShouldBe(10_000, tolerance: 0.001);
    }

    [Fact]
    public async Task Scoring_counts_a_listed_Reading_that_was_never_stored_as_missing()
    {
        await SeedAsync();
        await using var ingest = await StartIngestAsync();
        var run = await BenchRun.RunAsync(Fleet, Target, "score", rate: 30, TimeSpan.FromSeconds(3), during: null, TextWriter.Null, CancellationToken);
        var last = run.Manifest.Devices[0].Ranges[0];
        var lying = run.Manifest with
        {
            Devices = [run.Manifest.Devices[0] with { Ranges = [last with { LastSequence = last.LastSequence + 5 }] }, .. run.Manifest.Devices.Skip(1)],
        };

        var score = await Target.Database.ScoreAsync(lying, CancellationToken);

        score.Missing.ShouldBe(5);
        score.StoredDuplicates.ShouldBe(0);
    }

    public void Dispose()
    {
        _dataSource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
