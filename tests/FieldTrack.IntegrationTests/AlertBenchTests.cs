using System.Text.Json;
using FieldTrack.Simulator;
using FieldTrack.Simulator.Bench;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class AlertBenchTests(FieldTrackFixture fixture) : IntegrationTest(fixture), IDisposable
{
    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "ALB-001", "assetName": "Alert bench 1", "assetType": "Generator", "position": { "lat": 45.5, "lon": -122.6 }, "sensors": ["engineTempC"], "noise": { "engineTempC": 2.5 } },
          { "serial": "ALB-002", "assetName": "Alert bench 2", "assetType": "Generator", "position": { "lat": 45.5, "lon": -122.6 }, "sensors": ["engineTempC"], "noise": { "engineTempC": 2.5 } },
          { "serial": "ALB-003", "assetName": "Alert bench 3", "assetType": "Generator", "position": { "lat": 45.5, "lon": -122.6 }, "sensors": ["engineTempC"], "noise": { "engineTempC": 2.5 } }
        ] }
        """, AppContext.BaseDirectory);

    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-alert-bench-").FullName;
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(fixture.DatabaseConnectionString);

    private BenchTarget Target => new(
        Fixture.BrokerHost, Fixture.BrokerPort, new BenchDatabase(_dataSource), Path.Combine(_directory, "state.json"), Path.Combine(_directory, "results"), "no-such-compose-project");

    // The API of the host that runs ingest: its hub is the one ingest pushes through, and each host signs its own tokens.
    private static async Task<FieldTrackApi> SeedAsync(WebApplicationFactory<Program> host)
    {
        var admin = await FieldTrackApi.SignInAsync(host.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(admin, Fleet, TextWriter.Null, CancellationToken);
        return admin;
    }

    [Fact]
    public async Task Alert_latency_receives_every_Alert_over_SignalR_and_splits_the_time_at_CreatedAt()
    {
        await using var ingest = await StartIngestAsync();
        var admin = await SeedAsync(ingest);
        var token = await admin.CreateOperatorTokenAsync(ingest.CreateClient(), CancellationToken);

        var outcome = await AlertLatencyBench.RunAsync(
            Fleet, Target, admin, token, ingest.Server.BaseAddress, trials: 6, devices: 2,
            options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => ingest.Server.CreateHandler();
            },
            TextWriter.Null, CancellationToken);

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("alertsExpected").GetInt32().ShouldBe(6);
        results.GetProperty("alertsReceived").GetInt32().ShouldBe(6);
        results.GetProperty("missed").GetInt32().ShouldBe(0);
        var latency = results.GetProperty("latencyMs");
        latency.GetProperty("total").GetProperty("count").GetInt64().ShouldBe(6);
        latency.GetProperty("total").GetProperty("p95Ms").GetDouble().ShouldBeInRange(0, 10_000);
        // The total is publish to receive; server (publish to CreatedAt) and push (CreatedAt to receive) are its two parts, to within the clock-offset noise.
        var parts = latency.GetProperty("server").GetProperty("p50Ms").GetDouble() + latency.GetProperty("push").GetProperty("p50Ms").GetDouble();
        parts.ShouldBe(latency.GetProperty("total").GetProperty("p50Ms").GetDouble(), 250);
    }

    [Fact]
    public async Task The_hysteresis_A_B_runs_both_rules_on_the_same_Readings_and_the_Hysteresis_removes_false_Alerts()
    {
        await using var ingest = await StartIngestAsync();
        var admin = await SeedAsync(ingest);
        // A threshold at the sensor's mean: noise crosses it constantly, so only the Hysteresis keeps the rule from raising again and again.
        var scenario = Scenario.Parse("""{ "injections": [ { "device": "ALB-001", "kind": "overheat", "atSeconds": 3, "durationSeconds": 3, "amount": 100 } ] }""", Fleet);

        var outcome = await HysteresisBench.RunAsync(Fleet, Target, admin, scenario, threshold: 85, hysteresis: 8, seed: 1, rate: 60, TimeSpan.FromSeconds(8), TextWriter.Null, CancellationToken);

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("missing").GetInt64().ShouldBe(0);
        var plain = results.GetProperty("withoutHysteresis");
        var real = results.GetProperty("withHysteresis");
        plain.GetProperty("falseAlerts").GetInt32().ShouldBeGreaterThan(real.GetProperty("falseAlerts").GetInt32());
        results.GetProperty("falseAlertReductionPercent").GetDouble().ShouldBeGreaterThan(0);
        real.GetProperty("faultWindowsCaught").GetInt32().ShouldBe(1);
        // Reproducible: the result names the noise seed and the Boot each Device ran in.
        var parameters = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("parameters");
        parameters.GetProperty("seed").GetInt32().ShouldBe(1);
        parameters.GetProperty("boots").GetProperty("ALB-001").EnumerateArray().Select(boot => boot.GetInt32()).ShouldBe([1]);
        outcome.Pass.ShouldBeTrue();
    }

    [Fact]
    public async Task Offline_accuracy_detects_a_long_dropout_and_raises_nothing_for_a_short_one()
    {
        await using var ingest = await StartIngestAsync();
        var admin = await SeedAsync(ingest);
        // A rule of 3 s: ALB-001 is off for 12 s (long), ALB-002 has a 2 s Coverage gap (short).
        var scenario = Scenario.Parse(
            """
            { "injections": [
              { "device": "ALB-001", "kind": "powerOff", "atSeconds": 3, "durationSeconds": 12 },
              { "device": "ALB-002", "kind": "coverageGap", "atSeconds": 4, "durationSeconds": 2 }
            ] }
            """, Fleet);

        var outcome = await OfflineBench.RunAsync(Fleet, Target, admin, scenario, offlineSeconds: 3, rate: 60, TimeSpan.FromSeconds(22), TextWriter.Null, CancellationToken);

        var results = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("results");
        results.GetProperty("longDropouts").GetInt32().ShouldBe(1);
        results.GetProperty("shortDropouts").GetInt32().ShouldBe(1);
        results.GetProperty("detected").GetInt32().ShouldBe(1);
        results.GetProperty("falseAlarms").GetInt32().ShouldBe(0);
        results.GetProperty("missing").GetInt64().ShouldBe(0);
        outcome.Pass.ShouldBeTrue();
    }

    public void Dispose()
    {
        _dataSource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
