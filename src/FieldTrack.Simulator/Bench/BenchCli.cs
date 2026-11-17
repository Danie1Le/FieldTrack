using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>The <c>bench</c> command: reads its options, seeds the fleet and runs one benchmark against the running stack.</summary>
internal static class BenchCli
{
    // The compose file's database; the port is published on loopback only.
    private const string DefaultDatabase = "Host=localhost;Port=5432;Database=fieldtrack;Username=fieldtrack";

    // The compose file's well-known development password, used only against a database on this machine.
    private const string DevelopmentPassword = "fieldtrack";

    private static readonly string[] _loopback = ["localhost", "127.0.0.1", "::1"];

    /// <summary>Runs the benchmark <paramref name="kind"/> and prints its result.</summary>
    /// <returns>Whether the run met its pass bar.</returns>
    /// <exception cref="SimulatorException">The benchmark is unknown, an option is unusable, or the stack is not ready.</exception>
    public static async Task<bool> RunAsync(string kind, Options options, CancellationToken cancellationToken)
    {
        if (kind is not ("throughput" or "flood" or "crash" or "duplicates" or "seed-history" or "geofence" or "history"))
        {
            throw new SimulatorException($"Unknown benchmark '{kind}'. Use throughput, flood, crash, duplicates, seed-history, geofence or history.");
        }

        var rate = options.GetNumber("rate") ?? (kind == "crash" ? 500 : 2000);
        var duration = TimeSpan.FromSeconds(options.GetNumber("duration") ?? (kind == "flood" ? 60 : 600));
        var brokerHost = options.Get("mqtt-host") ?? "localhost";
        var brokerPort = (int)(options.GetNumber("mqtt-port") ?? 1883);
        var database = DatabaseConnection(options.Get("db"), Environment.GetEnvironmentVariable("FIELDTRACK_DB_PASSWORD"));
        // No default: on a shared machine a default project name points the result's environment (and a crash run's kills) at someone else's stack.
        var composeProject = options.Require("compose-project");

        // Before a run that may take ten minutes: a result whose SHA does not identify the code that produced it cannot be reproduced.
        if (options.Get("allow-dirty") != "true" && await BenchEnvironment.GitDirtyAsync(cancellationToken))
        {
            throw new SimulatorException("The working tree has uncommitted changes, so the git SHA in the result would not identify the code measured. Commit them, or pass --allow-dirty true for a trial run.");
        }

        await using var dataSource = NpgsqlDataSource.Create(database);
        var target = new BenchTarget(
            brokerHost, brokerPort, new BenchDatabase(dataSource), options.Get("state") ?? "simulator-state.json",
            options.Get("out") ?? Path.Combine("bench", "results"), composeProject);

        if (kind is "seed-history" or "geofence" or "history")
        {
            return await QueryAsync(kind, options, target, cancellationToken);
        }

        var fleet = FleetFile.Load(options.Require("fleet"));
        await SeedAsync(fleet, options, cancellationToken);
        Console.WriteLine($"Benchmark {kind}: {fleet.Devices.Count} Devices{(kind == "duplicates" ? string.Empty : $", {duration.TotalSeconds:0} s")}.");
        var outcome = kind switch
        {
            "flood" => await ThroughputBench.FloodAsync(fleet, target, duration, Console.Out, cancellationToken),
            "duplicates" => await DuplicateBench.RunAsync(fleet, target, (int)(options.GetNumber("count") ?? 10_000), Console.Out, cancellationToken),
            "crash" => await CrashAsync(fleet, target, options, rate, duration, cancellationToken),
            _ => await ThroughputBench.SustainedAsync(fleet, target, rate, duration, Console.Out, cancellationToken),
        };
        return Report(outcome);
    }

    // The history is seeded once (30 days of Device time, 200 Devices, a Reading every 8 minutes: 1.08 million); geofence and history then read it.
    private static async Task<bool> QueryAsync(string kind, Options options, BenchTarget target, CancellationToken cancellationToken)
    {
        var outcome = kind switch
        {
            "seed-history" => await SeedHistoryAsync(options, target, cancellationToken),
            "geofence" => await GeofenceBench.RunAsync(target, fraction: 0.01, QueryBench.WarmRuns, Console.Out, cancellationToken),
            _ => await HistoryBench.RunAsync(target, QueryBench.WarmRuns, Console.Out, cancellationToken),
        };
        return Report(outcome);
    }

    private static async Task<BenchOutcome> SeedHistoryAsync(Options options, BenchTarget target, CancellationToken cancellationToken)
    {
        var fleet = FleetFile.Load(options.Require("fleet"));
        var routeFiles = Directory.GetFiles(options.Get("routes") ?? Path.Combine("src", "FieldTrack.Simulator", "fleets", "routes"), "*.gpx").Order().ToList();
        if (routeFiles.Count == 0)
        {
            throw new SimulatorException("No GPX routes found: point --routes at a directory of .gpx files.");
        }

        await SeedAsync(fleet, options, cancellationToken);
        return await HistorySeeder.SeedAsync(
            fleet, [.. routeFiles.Select(GpxRoute.Load)], target, DateTimeOffset.UtcNow, TimeSpan.FromDays(options.GetNumber("days") ?? 30),
            TimeSpan.FromSeconds(options.GetNumber("interval") ?? 480), Console.Out, cancellationToken);
    }

    private static bool Report(BenchOutcome outcome)
    {
        Console.WriteLine(outcome.Json);
        Console.WriteLine($"{(outcome.Pass ? "PASS" : "FAIL")}: {Path.GetFullPath(outcome.Path)}");
        return outcome.Pass;
    }

    private static async Task<BenchOutcome> CrashAsync(
        FleetFile fleet, BenchTarget target, Options options, double rate, TimeSpan duration, CancellationToken cancellationToken)
    {
        var kills = CrashSchedule.Plan((int)(options.GetNumber("kills") ?? 10), duration, new Random());
        var ingest = await DockerIngest.FindAsync(target.ComposeProject, ApiAddress(options), cancellationToken);

        // A probe kill before anything is published: if /health does not go down, nothing a run would measure is a crash, so it does not start.
        var probe = await ingest.KillAndRestartAsync(cancellationToken);
        if (!probe.WentDown)
        {
            throw new SimulatorException($"Killing container {probe.Container} did not take {ApiAddress(options)}health down, so it is not the stack being measured. Not starting the run.");
        }

        return await CrashBench.RunAsync(fleet, target, rate, duration, kills, ingest.KillAndRestartAsync, Console.Out, cancellationToken);
    }

    private static Uri ApiAddress(Options options) => SimulatorCli.ApiAddress(options.Get("api") ?? "http://localhost:8080");

    /// <summary>
    /// The connection string for the platform's database: <paramref name="option"/> (<c>--db</c>, without a password) with the password from
    /// <paramref name="password"/> (<c>FIELDTRACK_DB_PASSWORD</c>). Passwords never come from the command line, which lands in shell history and process lists.
    /// </summary>
    /// <exception cref="SimulatorException">The option carries a password, or there is none and the database is not on this machine.</exception>
    internal static string DatabaseConnection(string? option, string? password)
    {
        var builder = new NpgsqlConnectionStringBuilder(option ?? DefaultDatabase);
        if (!string.IsNullOrEmpty(builder.Password))
        {
            throw new SimulatorException("--db must not carry a password: set FIELDTRACK_DB_PASSWORD.");
        }

        builder.Password = password
            ?? (_loopback.Contains(builder.Host, StringComparer.OrdinalIgnoreCase)
                ? DevelopmentPassword
                : throw new SimulatorException("Set FIELDTRACK_DB_PASSWORD: the development password only applies to a database on localhost."));
        return builder.ConnectionString;
    }

    private static async Task SeedAsync(FleetFile fleet, Options options, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = ApiAddress(options) };
        var api = await FieldTrackApi.SignInAsync(
            http, SimulatorCli.Credential("email", "FIELDTRACK_ADMIN_EMAIL", options), SimulatorCli.Credential(null, "FIELDTRACK_ADMIN_PASSWORD", options), cancellationToken);
        await Seeder.SeedAsync(api, fleet, TextWriter.Null, cancellationToken);
    }
}
