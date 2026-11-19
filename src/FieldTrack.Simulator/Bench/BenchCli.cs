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
        if (kind is not ("throughput" or "flood" or "crash" or "duplicates" or "seed-history" or "geofence" or "history" or "alert-latency" or "offline" or "hysteresis"))
        {
            throw new SimulatorException(
                $"Unknown benchmark '{kind}'. Use throughput, flood, crash, duplicates, seed-history, geofence, history, alert-latency, offline or hysteresis.");
        }

        // The other benchmarks read the local broker's $SYS counters or the database, so only alert-latency can measure a stack in the cloud.
        string[] cloudOnly = ["clock-offset-ms", "mqtt-username", "mqtt-path"];
        if (kind != "alert-latency" && (options.Get("mqtt-transport") is not (null or "tcp") || cloudOnly.Any(name => options.Get(name) is not null)))
        {
            throw new SimulatorException(
                $"--mqtt-transport ws|wss, --clock-offset-ms, --mqtt-username and --mqtt-path apply to alert-latency only; {kind} needs the local stack's broker and database.");
        }

        var alertBench = kind is "alert-latency" or "offline" or "hysteresis";
        var rate = options.GetNumber("rate") ?? (kind == "crash" ? 500 : alertBench ? AlertBenches.DefaultRate : 2000);
        var duration = TimeSpan.FromSeconds(options.GetNumber("duration") ?? (kind == "flood" ? 60 : alertBench ? AlertBenches.DefaultDurationSeconds : 600));
        await using var target = TargetFor(kind, options);

        // Before a run that may take ten minutes: a result whose SHA does not identify the code that produced it cannot be reproduced.
        if (options.Get("allow-dirty") != "true" && await BenchEnvironment.GitDirtyAsync(cancellationToken))
        {
            throw new SimulatorException("The working tree has uncommitted changes, so the git SHA in the result would not identify the code measured. Commit them, or pass --allow-dirty true for a trial run.");
        }

        if (kind is "seed-history" or "geofence" or "history")
        {
            return await QueryAsync(kind, options, target, cancellationToken);
        }

        var fleet = FleetFile.Load(options.Require("fleet"));
        if (alertBench)
        {
            return Report(await AlertBenches.RunAsync(kind, fleet, target, options, ApiAddress(options), rate, duration, cancellationToken));
        }

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

    /// <summary>
    /// What the benchmark <paramref name="kind"/> measures. An alert-latency run whose <c>--api</c> is not on this machine measures a deployed
    /// platform: it has no database or compose project, so neither option is needed and the result describes no local stack.
    /// </summary>
    /// <exception cref="SimulatorException">An option is unusable, or a remote run has no <c>--clock-offset-ms</c>.</exception>
    internal static BenchTarget TargetFor(string kind, Options options)
    {
        var (host, port) = (options.Get("mqtt-host") ?? "localhost", (int)(options.GetNumber("mqtt-port") ?? 1883));
        var state = options.Get("state") ?? "simulator-state.json";
        var output = options.Get("out") ?? Path.Combine("bench", "results");
        if (kind == "alert-latency" && !ApiAddress(options).IsLoopback)
        {
            // Without a database the offset between the platform's clock and this machine's cannot be measured, so it must be given.
            return options.Get("clock-offset-ms") is null
                ? throw new SimulatorException("--api is not on this machine, so there is no database to measure the clock offset on: pass --clock-offset-ms.")
                : new BenchTarget(host, port, LocalDatabase: null, state, output, LocalComposeProject: null);
        }

        var connection = DatabaseConnection(options.Get("db"), Environment.GetEnvironmentVariable("FIELDTRACK_DB_PASSWORD"));
        // No default: on a shared machine a default project name points the result's environment (and a crash run's kills) at someone else's stack.
        var composeProject = options.Require("compose-project");
        return new BenchTarget(host, port, new BenchDatabase(NpgsqlDataSource.Create(connection)), state, output, composeProject);
    }

    // The history is seeded once (30 days of Device time, 200 Devices, a Reading every 8 minutes: 1.08 million); geofence and history then read it.
    private static async Task<bool> QueryAsync(string kind, Options options, BenchTarget target, CancellationToken cancellationToken)
    {
        var outcome = kind switch
        {
            "seed-history" => await SeedHistoryAsync(options, target, cancellationToken),
            "geofence" => await GeofenceBench.RunAsync(
                target, fraction: 0.01, Vertices(options.GetNumber("vertices")), QueryBench.WarmRuns, await PostgresRestartAsync(target, cancellationToken), Console.Out, cancellationToken),
            _ => await HistoryBench.RunAsync(target, QueryBench.WarmRuns, await PostgresRestartAsync(target, cancellationToken), Console.Out, cancellationToken),
        };
        return Report(outcome);
    }

    // Fails before anything is restarted when --compose-project or --db point at the wrong database.
    private static async Task<Func<CancellationToken, Task>> PostgresRestartAsync(BenchTarget target, CancellationToken cancellationToken) =>
        (await DockerPostgres.FindAsync(target.ComposeProject, target.Database.DataSource, cancellationToken)).RestartAsync;

    /// <summary>The geofence shape's position count from <c>--vertices</c>, defaulting to the headline square.</summary>
    /// <exception cref="SimulatorException">The value is not a whole number.</exception>
    internal static int Vertices(double? value) => value switch
    {
        null => GeofenceBench.SquareVertices,
        { } number when double.IsInteger(number) => (int)Math.Clamp(number, -1, 100_000),
        _ => throw new SimulatorException($"--vertices must be a whole number, got {value}."),
    };

    private static async Task<BenchOutcome> SeedHistoryAsync(Options options, BenchTarget target, CancellationToken cancellationToken)
    {
        var fleet = FleetFile.Load(options.Require("fleet"));
        var routeFiles = Directory.GetFiles(options.Get("routes") ?? Path.Combine("src", "FieldTrack.Simulator", "fleets", "routes"), "*.gpx").Order().ToList();
        if (routeFiles.Count == 0)
        {
            throw new SimulatorException("No GPX routes found: point --routes at a directory of .gpx files.");
        }

        // Before SeedAsync creates anything through the API.
        var (span, interval) = HistoryWindow(options.GetNumber("days"), options.GetNumber("interval"));
        await SeedAsync(fleet, options, cancellationToken);
        return await HistorySeeder.SeedAsync(
            fleet, [.. routeFiles.Select(GpxRoute.Load)], target, DateTimeOffset.UtcNow, span, interval, Console.Out, cancellationToken);
    }

    /// <summary>
    /// The platform clock minus this machine's from <c>--clock-offset-ms</c>, or null to measure it on the database. A cloud database is out
    /// of reach, so the offset is given instead (for example this machine against NTP, assuming the cloud hosts keep NTP time).
    /// </summary>
    /// <exception cref="SimulatorException">The value is not finite or more than an hour either way.</exception>
    internal static TimeSpan? ClockOffset(double? milliseconds) => milliseconds switch
    {
        null => null,
        { } ms when double.IsFinite(ms) && Math.Abs(ms) <= TimeSpan.FromHours(1).TotalMilliseconds => TimeSpan.FromMilliseconds(ms),
        _ => throw new SimulatorException($"--clock-offset-ms must be a number of milliseconds within an hour either way, got {milliseconds}."),
    };

    /// <summary>The history's length and spacing from <c>--days</c> and <c>--interval</c> (seconds), defaulting to 30 days and 480 s.</summary>
    /// <exception cref="SimulatorException">A value is not finite, <paramref name="days"/> is not positive, or <paramref name="intervalSeconds"/> is under one second.</exception>
    internal static (TimeSpan Span, TimeSpan Interval) HistoryWindow(double? days, double? intervalSeconds)
    {
        var (d, s) = (days ?? 30, intervalSeconds ?? 480);
        // Zero, negative, NaN or sub-tick intervals never reach the end of the window: the COPY would stream rows until the disk is full.
        if (!double.IsFinite(d) || d <= 0 || !double.IsFinite(s) || s < 1)
        {
            throw new SimulatorException($"--days must be a positive number and --interval at least 1 (seconds), got {d} and {s}.");
        }

        return (TimeSpan.FromDays(d), TimeSpan.FromSeconds(s));
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
        // Npgsql applies the command timeout to every write of a COPY stream, and a busy server stops reading it while it maintains indexes
        // (seed-history). A timeout the user put in --db is their choice and kept.
        if (!builder.ConnectionString.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase))
        {
            builder.CommandTimeout = BenchDatabase.CommandTimeoutSeconds;
        }

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
