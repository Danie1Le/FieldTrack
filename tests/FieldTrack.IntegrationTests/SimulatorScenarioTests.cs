using System.Text.Json;
using FieldTrack.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

/// <summary>A simulator run that injects Faults, Power offs, Coverage gaps and clock problems, checked against what ingest stored.</summary>
public sealed class SimulatorScenarioTests(FieldTrackFixture fixture) : IntegrationTest(fixture), IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-scenario-").FullName;

    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "TRK-001", "assetName": "Truck 1", "assetType": "Truck", "position": { "lat": 45.5231, "lon": -122.6765 },
            "sensors": ["engineTempC", "fuelPct"] },
          { "serial": "GEN-001", "assetName": "Generator 1", "assetType": "Generator", "position": { "lat": 45.5262, "lon": -122.6742 },
            "sensors": ["batteryV"] },
          { "serial": "SUB-001", "assetName": "Substation 1", "assetType": "Sensor", "position": { "lat": 45.5300, "lon": -122.6700 },
            "sensors": ["batteryV"] }
        ] }
        """, AppContext.BaseDirectory);

    private RunOptions Options(TimeSpan duration, double? rate, Scenario? scenario = null) =>
        new(Fixture.BrokerHost, Fixture.BrokerPort, rate, duration, Path.Combine(_directory, "state.json"), Path.Combine(_directory, "manifest.json"), scenario);

    private async Task<Dictionary<string, JsonElement>> SeedAsync()
    {
        var api = await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(api, Fleet, TextWriter.Null, CancellationToken);
        using var admin = await AdminClientAsync();
        return (await ListAsync(admin, "/assets")).ToDictionary(asset => asset.GetProperty("name").GetString()!);
    }

    private static async Task<JsonElement[]> ReadingsAsync(HttpClient admin, JsonElement asset) =>
        await ListAsync(admin, $"/assets/{asset.GetProperty("id").GetString()}/readings?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}");

    private static long Published(RunManifest manifest, string serial) =>
        manifest.Devices.Single(device => device.Serial == serial).Ranges.Sum(range => range.LastSequence - range.FirstSequence + 1);

    [Fact]
    public async Task A_scripted_run_injects_what_the_Scenario_says_and_the_Manifest_and_stored_Readings_agree_with_it()
    {
        var assets = await SeedAsync();
        await using var ingest = await StartIngestAsync();
        using var admin = await AdminClientAsync(ingest);
        var scenario = Scenario.Parse("""
            { "injections": [
              { "device": "TRK-001", "kind": "overheat", "atSeconds": 0, "durationSeconds": 8, "amount": 130 },
              { "device": "TRK-001", "kind": "powerOff", "atSeconds": 1.55, "durationSeconds": 1 },
              { "device": "SUB-001", "kind": "coverageGap", "atSeconds": 1.55, "durationSeconds": 1.3 },
              { "device": "GEN-001", "kind": "clockJump", "atSeconds": 1.55, "amount": 900 }
            ] }
            """, Fleet);

        var manifest = await FleetRunner.RunAsync(Fleet, Options(TimeSpan.FromSeconds(5), rate: 30, scenario), TextWriter.Null, TimeProvider.System, CancellationToken);

        // The Manifest names every window, with the times the run applied them.
        var windows = manifest.Devices.ToDictionary(device => device.Serial, device => device.Windows);
        windows["TRK-001"].Select(window => window.Kind).ShouldBe([InjectionKind.Overheat, InjectionKind.PowerOff]);
        var powerOff = windows["TRK-001"][1];
        powerOff.Start.ShouldBe(manifest.StartedAt + TimeSpan.FromSeconds(1.55));
        powerOff.End.ShouldBe(manifest.StartedAt + TimeSpan.FromSeconds(1.55) + TimeSpan.FromSeconds(1));
        var gap = windows["SUB-001"].ShouldHaveSingleItem();
        gap.Kind.ShouldBe(InjectionKind.CoverageGap);
        var jump = windows["GEN-001"].ShouldHaveSingleItem();
        jump.End.ShouldBe(manifest.EndedAt, "a jump lasts to the end of the run");

        // Power off: a new Boot restarts the Sequence numbers and nothing was taken or sent inside the window.
        var truck = manifest.Devices.Single(device => device.Serial == "TRK-001").Ranges;
        truck.Select(range => (range.Boot, range.FirstSequence)).ShouldBe([(1, 0L), (2, 0L)]);
        var storedTruck = await EventuallyAsync(() => ReadingsAsync(admin, assets["Truck 1"]), stored => stored.Length >= Published(manifest, "TRK-001"));
        storedTruck.Length.ShouldBe((int)Published(manifest, "TRK-001"));
        foreach (var range in truck)
        {
            storedTruck.Where(reading => reading.GetProperty("boot").GetInt32() == range.Boot)
                .Select(reading => reading.GetProperty("sequenceNumber").GetInt64())
                .ShouldBe(Enumerable.Range(0, (int)(range.LastSequence + 1)).Select(i => (long)i));
        }

        storedTruck.ShouldNotContain(reading => reading.GetProperty("deviceTime").GetDateTimeOffset() >= powerOff.Start && reading.GetProperty("deviceTime").GetDateTimeOffset() < powerOff.End);
        storedTruck.Max(reading => reading.GetProperty("engineTempC").GetDouble()).ShouldBeGreaterThan(100, "the overheat ramps well past normal");

        // Coverage gap: everything taken is stored, and what was taken inside the gap arrived only after it.
        var storedSubstation = await EventuallyAsync(() => ReadingsAsync(admin, assets["Substation 1"]), stored => stored.Length >= Published(manifest, "SUB-001"));
        storedSubstation.Select(reading => reading.GetProperty("sequenceNumber").GetInt64()).ShouldBe(Enumerable.Range(0, (int)Published(manifest, "SUB-001")).Select(i => (long)i));
        var kept = storedSubstation.Where(reading => reading.GetProperty("deviceTime").GetDateTimeOffset() >= gap.Start && reading.GetProperty("deviceTime").GetDateTimeOffset() < gap.End).ToList();
        kept.Count.ShouldBeGreaterThan(5, "10 Readings a second for 1.3 s");
        kept.ShouldAllBe(reading => reading.GetProperty("receivedTime").GetDateTimeOffset() >= gap.End);

        // Clock jump: ingest refuses every Reading timed more than 5 minutes ahead, so only the ones before the jump are stored.
        var (storedGenerator, rejected) = await EventuallyAsync(
            async () => (await ReadingsAsync(admin, assets["Generator 1"]), await ListAsync(admin, "/rejected-messages")),
            seen => seen.Item1.Length + seen.Item2.Length >= Published(manifest, "GEN-001"));
        rejected.ShouldNotBeEmpty();
        rejected.ShouldAllBe(message => message.GetProperty("reason").GetString() == "TooFarAhead");
        storedGenerator.ShouldNotBeEmpty();
        storedGenerator.ShouldAllBe(reading => reading.GetProperty("deviceTime").GetDateTimeOffset() < jump.Start.AddMilliseconds(50));
        (storedGenerator.Length + rejected.Length).ShouldBe((int)Published(manifest, "GEN-001"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Maximum_mode_publishes_as_fast_as_the_broker_acknowledges_and_the_Manifest_has_no_target_rate(int run)
    {
        var assets = await SeedAsync();
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var meter = new System.Diagnostics.Metrics.MeterListener();
        meter.InstrumentPublished = (instrument, listener) => { if (instrument.Name is "fieldtrack.ingest.batch_size" or "fieldtrack.ingest.batch_commit_duration") { listener.EnableMeasurementEvents(instrument); } };
        meter.SetMeasurementEventCallback<long>((instrument, value, _, _) => logs.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} BATCH size {value}"));
        meter.SetMeasurementEventCallback<double>((instrument, value, _, _) => logs.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} BATCH commit {value * 1000:F0} ms"));
        meter.Start();
        await using var ingest = await StartIngestAsync(services: collection => collection.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(new CollectingLogger(logs)));

        var manifest = await FleetRunner.RunAsync(Fleet, Options(TimeSpan.FromSeconds(2), rate: null), TextWriter.Null, TimeProvider.System, CancellationToken);

        manifest.RatePerSecond.ShouldBeNull();
        var total = Fleet.Devices.Sum(device => Published(manifest, device.Serial));
        total.ShouldBeGreaterThan(90, "a paced run of 2 s at 30 a second publishes about 60");

        var truck = Guid.Parse(assets["Truck 1"].GetProperty("id").GetString()!);
        await using var database = await OpenDatabaseAsync();
        var timeline = new List<string>();
        var started = DateTime.UtcNow;
        long stored = 0;
        for (var i = 0; i < 20; i++)
        {
            stored = (long)(await SqlAsync(database, "SELECT count(*) FROM readings WHERE asset_id = @p0", truck))!;
            var all = (long)(await SqlAsync(database, "SELECT count(*) FROM readings"))!;
            var keys = (long)(await SqlAsync(database, "SELECT count(*) FROM reading_keys"))!;
            var rejected = (long)(await SqlAsync(database, "SELECT count(*) FROM rejected_messages"))!;
            timeline.Add($"{(DateTime.UtcNow - started).TotalSeconds:F0}s trk={stored} all={all} keys={keys} rejected={rejected}");
            if (stored >= Published(manifest, "TRK-001"))
            {
                break;
            }

            await Task.Delay(1000, CancellationToken);
        }

        var states = string.Join("; ", await ActivityAsync(database));
        var counts = string.Join(",", Fleet.Devices.Select(d => $"{d.Serial}={Published(manifest, d.Serial)}"));
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "max-diag.log"), $"run {run} published={counts} stored={stored}\n");
        stored.ShouldBe(Published(manifest, "TRK-001"), $"run {run} published {counts}\nTIMELINE\n{string.Join("\n", timeline)}\nPG {states}\nLOGS\n{string.Join("\n", logs.Where(l => !l.Contains("DbCommand")).TakeLast(60))}");
    }

    private static async Task<List<string>> ActivityAsync(NpgsqlConnection database)
    {
        var result = new List<string>();
        await using var command = new NpgsqlCommand("SELECT state, wait_event_type, wait_event, left(query, 60) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", database);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        while (await reader.ReadAsync(CancellationToken))
        {
            result.Add($"[{reader.GetValue(0)}|{reader.GetValue(1)}|{reader.GetValue(2)}|{reader.GetValue(3)}]");
        }

        return result;
    }

    private sealed class CollectingLogger(System.Collections.Concurrent.ConcurrentQueue<string> lines) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {eventId.Id} {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
