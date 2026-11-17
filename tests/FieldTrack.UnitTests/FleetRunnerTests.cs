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
            () => FleetRunner.RunAsync(fleet, options, log, new FailingOnceTimeProvider(failingCall: 2), TestContext.Current.CancellationToken));

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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // GetUtcNow is called once when the run starts and once per Reading, so failing the second call fails the Device mid-run.
    private sealed class FailingOnceTimeProvider(int failingCall) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _calls) == failingCall ? throw new InvalidOperationException("clock broke") : System.GetUtcNow();
    }
}
