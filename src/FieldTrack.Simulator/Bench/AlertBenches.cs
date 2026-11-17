namespace FieldTrack.Simulator.Bench;

/// <summary>The command line of the alert benchmarks (#45): <c>alert-latency</c>, <c>offline</c> and <c>hysteresis</c>.</summary>
internal static class AlertBenches
{
    /// <summary>Readings per second the Scenario benchmarks publish by default: a demo rate, not a load test.</summary>
    public const double DefaultRate = 10;

    /// <summary>How long the Scenario benchmarks run by default; the committed Scenarios fit in it.</summary>
    public const double DefaultDurationSeconds = 300;

    private const string FleetDirectory = "src/FieldTrack.Simulator/fleets";

    /// <summary>Seeds <paramref name="fleet"/>, runs the benchmark <paramref name="kind"/> and writes its result.</summary>
    /// <exception cref="SimulatorException">An option is unusable, or the stack is not ready.</exception>
    public static async Task<BenchOutcome> RunAsync(
        string kind, FleetFile fleet, BenchTarget target, Options options, Uri api, double rate, TimeSpan duration, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = api };
        var admin = await FieldTrackApi.SignInAsync(
            http, SimulatorCli.Credential("email", "FIELDTRACK_ADMIN_EMAIL", options), SimulatorCli.Credential(null, "FIELDTRACK_ADMIN_PASSWORD", options), cancellationToken);
        await Seeder.SeedAsync(admin, fleet, TextWriter.Null, cancellationToken);
        Console.WriteLine($"Benchmark {kind}: {fleet.Devices.Count} Devices.");

        if (kind == "alert-latency")
        {
            using var anonymous = new HttpClient { BaseAddress = api };
            var token = await admin.CreateOperatorTokenAsync(anonymous, cancellationToken);
            var devices = (int)(options.GetNumber("devices") ?? 10);
            var alerts = (int)(options.GetNumber("alerts") ?? 200);
            if (devices < 1 || alerts < 1)
            {
                throw new SimulatorException("--devices and --alerts must be at least 1.");
            }

            return await AlertLatencyBench.RunAsync(fleet, target, admin, token, api, alerts, devices, configureHub: null, Console.Out, cancellationToken);
        }

        var scenario = Scenario.Load(options.Get("scenario") ?? Path.Combine(FleetDirectory, $"alerts-{kind}-scenario.json"), fleet);
        return kind == "offline"
            ? await OfflineBench.RunAsync(fleet, target, admin, scenario, (int)(options.GetNumber("offline-seconds") ?? 20), rate, duration, Console.Out, cancellationToken)
            : await HysteresisBench.RunAsync(
                fleet, target, admin, scenario, options.GetNumber("threshold") ?? 90, options.GetNumber("hysteresis") ?? 8, rate, duration, Console.Out, cancellationToken);
    }
}
