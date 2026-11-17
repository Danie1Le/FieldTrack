using System.Text.Json;
using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class FleetRunnerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-runner-").FullName;

    [Fact(Timeout = 30_000)]
    public async Task A_Device_that_fails_stops_the_run_and_the_Manifest_is_still_written()
    {
        var fleet = FleetFile.Parse("""{ "devices": [ { "serial": "TRK-001", "assetType": "Truck" } ] }""", _directory);
        var manifestPath = Path.Combine(_directory, "manifest.json");
        // The broker is never reached: the Device fails building its first Reading, before it connects.
        var options = new RunOptions("127.0.0.1", 1, 10, Duration: null, Path.Combine(_directory, "state.json"), manifestPath);
        var log = new StringWriter();

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => FleetRunner.RunAsync(fleet, options, log, new StumblingClock(failingTimestamp: 2, TimeSpan.Zero), TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("clock broke");
        log.ToString().ShouldContain("TRK-001: stopped (clock broke); stopping the run");
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Maximum_mode_injects_nothing_so_a_Scenario_or_random_rates_are_refused()
    {
        var fleet = FleetFile.Parse("""{ "devices": [ { "serial": "TRK-001", "assetType": "Truck", "sensors": ["fuelPct"] } ] }""", _directory);
        var scenario = Scenario.Parse("""{ "injections": [ { "device": "TRK-001", "kind": "powerOff", "atSeconds": 1, "durationSeconds": 1 } ] }""", fleet);
        var state = Path.Combine(_directory, "state.json");
        var manifest = Path.Combine(_directory, "manifest.json");

        var withScenario = await Should.ThrowAsync<SimulatorException>(
            () => FleetRunner.RunAsync(fleet, new RunOptions("127.0.0.1", 1, null, null, state, manifest, scenario), TextWriter.Null, TimeProvider.System, TestContext.Current.CancellationToken));
        var withRates = await Should.ThrowAsync<SimulatorException>(
            () => FleetRunner.RunAsync(fleet, new RunOptions("127.0.0.1", 1, null, null, state, manifest, null, new RandomRates(1, 0, 0)), TextWriter.Null, TimeProvider.System, TestContext.Current.CancellationToken));

        withScenario.Message.ShouldContain("--rate max");
        withRates.Message.ShouldContain("--rate max");
        File.Exists(state).ShouldBeFalse("nothing started, so no Boot was raised");
    }

    [Fact(Timeout = 30_000)]
    public async Task The_Manifest_times_the_run_by_the_elapsed_clock_so_a_wall_clock_step_does_not_stretch_it()
    {
        var fleet = FleetFile.Parse("""{ "devices": [ { "serial": "TRK-001", "assetType": "Truck" } ] }""", _directory);
        var manifestPath = Path.Combine(_directory, "manifest.json");
        var options = new RunOptions("127.0.0.1", 1, 10, Duration: null, Path.Combine(_directory, "state.json"), manifestPath);

        // The wall clock jumps an hour after the run starts, as after an NTP correction or a VM resume.
        await Should.ThrowAsync<InvalidOperationException>(
            () => FleetRunner.RunAsync(fleet, options, TextWriter.Null, new StumblingClock(failingTimestamp: 2, TimeSpan.FromHours(1)), TestContext.Current.CancellationToken));

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var length = manifest.RootElement.GetProperty("endedAt").GetDateTimeOffset() - manifest.RootElement.GetProperty("startedAt").GetDateTimeOffset();
        length.ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // GetTimestamp is read once when the run starts and once per Reading, so failing the second read fails the Device mid-run.
    // Every wall-clock read after the first is off by wallClockStep.
    private sealed class StumblingClock(int failingTimestamp, TimeSpan wallClockStep) : TimeProvider
    {
        private int _timestamps;
        private int _wallClockReads;

        public override long GetTimestamp() =>
            Interlocked.Increment(ref _timestamps) == failingTimestamp ? throw new InvalidOperationException("clock broke") : System.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + (Interlocked.Increment(ref _wallClockReads) > 1 ? wallClockStep : TimeSpan.Zero);
    }
}
