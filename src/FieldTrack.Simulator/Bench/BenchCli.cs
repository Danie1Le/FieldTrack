using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>The <c>bench</c> command: reads its options, seeds the fleet and runs one benchmark against the running stack.</summary>
internal static class BenchCli
{
    // The compose file's database and its well-known development credentials; the port is published on loopback only.
    private const string DefaultDatabase = "Host=localhost;Port=5432;Database=fieldtrack;Username=fieldtrack;Password=fieldtrack";

    /// <summary>Runs the benchmark <paramref name="kind"/> and prints its result.</summary>
    /// <returns>Whether the run met its pass bar.</returns>
    /// <exception cref="SimulatorException">The benchmark is unknown, an option is unusable, or the stack is not ready.</exception>
    public static async Task<bool> RunAsync(string kind, Options options, CancellationToken cancellationToken)
    {
        if (kind is not ("throughput" or "flood" or "crash" or "duplicates"))
        {
            throw new SimulatorException($"Unknown benchmark '{kind}'. Use throughput, flood, crash or duplicates.");
        }

        var fleet = FleetFile.Load(options.Require("fleet"));
        var rate = options.GetNumber("rate") ?? (kind == "crash" ? 500 : 2000);
        var duration = TimeSpan.FromSeconds(options.GetNumber("duration") ?? (kind == "flood" ? 60 : 600));
        var broker = (options.Get("mqtt-host") ?? "localhost", (int)(options.GetNumber("mqtt-port") ?? 1883));

        await SeedAsync(fleet, options, cancellationToken);
        await using var dataSource = NpgsqlDataSource.Create(options.Get("db") ?? DefaultDatabase);
        var target = new BenchTarget(
            broker.Item1, broker.Item2, new BenchDatabase(dataSource), options.Get("state") ?? "simulator-state.json",
            options.Get("out") ?? Path.Combine("bench", "results"), options.Get("compose-project") ?? "fieldtrack");

        Console.WriteLine($"Benchmark {kind}: {fleet.Devices.Count} Devices{(kind == "duplicates" ? string.Empty : $", {duration.TotalSeconds:0} s")}.");
        var outcome = kind switch
        {
            "flood" => await ThroughputBench.FloodAsync(fleet, target, duration, Console.Out, cancellationToken),
            "duplicates" => await DuplicateBench.RunAsync(fleet, target, (int)(options.GetNumber("count") ?? 10_000), Console.Out, cancellationToken),
            "crash" => await CrashAsync(fleet, target, options, rate, duration, cancellationToken),
            _ => await ThroughputBench.SustainedAsync(fleet, target, rate, duration, Console.Out, cancellationToken),
        };
        Console.WriteLine(outcome.Json);
        Console.WriteLine($"{(outcome.Pass ? "PASS" : "FAIL")}: {Path.GetFullPath(outcome.Path)}");
        return outcome.Pass;
    }

    private static async Task<BenchOutcome> CrashAsync(
        FleetFile fleet, BenchTarget target, Options options, double rate, TimeSpan duration, CancellationToken cancellationToken)
    {
        var kills = CrashSchedule.Plan((int)(options.GetNumber("kills") ?? 10), duration, new Random());
        var ingest = await DockerIngest.FindAsync(target.ComposeProject, ApiAddress(options), cancellationToken);
        return await CrashBench.RunAsync(fleet, target, rate, duration, kills, ingest.KillAndRestartAsync, Console.Out, cancellationToken);
    }

    private static Uri ApiAddress(Options options) => SimulatorCli.ApiAddress(options.Get("api") ?? "http://localhost:8080");

    private static async Task SeedAsync(FleetFile fleet, Options options, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = ApiAddress(options) };
        var api = await FieldTrackApi.SignInAsync(
            http, SimulatorCli.Credential("email", "FIELDTRACK_ADMIN_EMAIL", options), SimulatorCli.Credential(null, "FIELDTRACK_ADMIN_PASSWORD", options), cancellationToken);
        await Seeder.SeedAsync(api, fleet, TextWriter.Null, cancellationToken);
    }
}
