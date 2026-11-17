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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // GetUtcNow is called once when the run starts and once per Reading, so failing the second call fails the Device mid-run.
    private sealed class FailingOnceTimeProvider(int failingCall) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _calls) == failingCall ? throw new InvalidOperationException("clock broke") : System.GetUtcNow();
    }
}
