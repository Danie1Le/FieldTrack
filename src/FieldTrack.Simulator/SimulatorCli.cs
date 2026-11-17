using FieldTrack.Simulator.Bench;
using System.Text.Json;

namespace FieldTrack.Simulator;

/// <summary>
/// The simulator's command line. It drives demos and benchmarks: <c>seed</c> prepares the platform from a fleet file
/// and <c>run</c> publishes Readings for it (spec #21, Simulator and bench).
/// </summary>
internal static class SimulatorCli
{
    // CancellationTokenSource.CancelAfter takes at most about 49.7 days.
    private const double MaxDurationSeconds = 4_000_000;

    private const string Usage = """
        FieldTrack simulator

          seed --fleet <file> [--api http://localhost:8080] [--email <admin email>]
              Creates the fleet's Assets, Devices and Attachments through the REST API as Admin. Safe to repeat.

          run --fleet <file> --rate <readings per second | max> [--duration <seconds>] [--mqtt-host localhost] [--mqtt-port 1883]
              [--state simulator-state.json] [--manifest manifest-<time>.json]
              [--scenario <file>] [--fault-rate <n>] [--power-off-rate <n>] [--coverage-gap-rate <n>]
              Publishes Readings for every Device at the target aggregate rate, spread evenly across Devices, until
              --duration passes or Ctrl+C. --rate max publishes as fast as the broker acknowledges, with noise and no
              injections. Each start raises every Device's Boot (kept in --state) and restarts its Sequence numbers
              at 0. --scenario injects the Faults, Power offs, Coverage gaps and clock problems a file scripts (seconds
              from the start); the three rates (events per Device-hour) inject them at random, dropping a draw that
              would overlap one already running, so long windows at high rates come in under their rate. Only paced
              runs inject. The Manifest lists what was published and every injection window the run applied. MQTT is plain TCP without credentials: point it only at a local or
              trusted-network broker.

          bench throughput|flood|crash|duplicates --fleet <file> [--rate <readings per second>] [--duration <seconds>]
              Seeds the fleet, then measures telemetry ingest against the running compose stack and writes JSON (metrics plus git SHA,
              CPU, RAM, OS, Docker limits, Postgres settings and image tags) to --out (bench/results). `throughput` holds --rate
              (default 2000) for --duration (default 600) and passes when the fleet achieves the rate (within 2%), ingest latency p95 < 200 ms,
              the broker backlog stays flat and nothing is lost; `flood` publishes flat out for --duration (default 60) and reports the rate
              ingest absorbed; `crash` publishes --rate (default 500) for --duration (default 600) while `docker kill` hits the ingest (api)
              container of --compose-project --kills times (default 10) at random moments, and passes with 0 missing and 0 duplicate
              Readings against the Manifest and the broker's received count, and only if every kill took the measured stack's /health down;
              `duplicates` publishes --count (default 10000) Readings, republishes the identical bytes, and passes when that many are
              stored and the Reading key table refused every copy (the broker's backlog must be empty and nothing else may publish meanwhile).
              Requires --compose-project <name>, the stack that --api serves. Also: [--db <connection string without password>]
              [--mqtt-host --mqtt-port --api --email --state --out]
              The database password comes from FIELDTRACK_DB_PASSWORD (the compose development password only against localhost).
              A run refuses a working tree with uncommitted changes unless --allow-dirty true is given.
              Start the stack with bench/docker-compose.bench.yml. Exit code 3 means the run finished but missed its bar.

          bench seed-history|geofence|history --compose-project <name> [--fleet <file> --routes <dir> --days 30 --interval 480 --vertices 5]
              The query benchmarks (#46), on an empty database. `seed-history` seeds the fleet (200 Devices in fleets/bench.json), then
              COPYs a Reading every --interval seconds (default 480) per Device for --days (default 30) ending now, along the GPX routes in
              --routes (default src/FieldTrack.Simulator/fleets/routes), bypassing MQTT: about 1.08 million Readings. It also builds
              readings_unpartitioned, a copy with the same indexes. `geofence` counts the Readings inside a Geofence holding about 1% of them with
              the GiST index and with index scans off. The 50 ms bar (warm median with the index) applies to the headline shape only: a
              square of --vertices positions (default 5, a fence drawn around a site). A 65-position circle of the same 1% is measured too and
              reported without a bar. `history` reads one Asset's 24 hours from the partitioned and the unpartitioned table, requires the
              same rows from both and reports the difference. Each records EXPLAIN (ANALYZE, BUFFERS) timings: a cold run, then the median
              of 10 warm runs. `geofence` and `history` restart the compose postgres container before every cold run (the OS page cache is
              not flushed).

        bench alert-latency|offline|hysteresis --fleet <file> --compose-project <name> [--scenario <file>] [--rate 10 --duration 300]
              The alert benchmarks (#45), on a stack whose ingest is running; they seed the fleet (fleets/alerts.json) and create their own Alert
              Rules. `alert-latency` publishes --alerts (default 200) Readings that breach a Threshold rule, over the first --devices (default 10)
              Devices, and listens on SignalR as a new Operator: publish-to-receive p95 must be under 500 ms, split at CreatedAt into server and
              push time. `offline` runs --scenario (default fleets/alerts-offline-scenario.json: long and short Power offs and Coverage gaps)
              against an Offline rule of --offline-seconds (default 20): every long dropout needs an Alert created within the last Received time
              plus those seconds plus 2 s, and any Alert without a long dropout is a false alarm. `hysteresis` runs --scenario (default
              fleets/alerts-hysteresis-scenario.json, noisy engine temperature) against two Threshold rules (above --threshold, default 90) with
              Hysteresis 0 and --hysteresis (default 8) at once, so both see the same Readings; a false Alert is one outside every Fault window
              of the Manifest. Use a stack not shared with another benchmark: Alerts from earlier runs and rules stay in the database.

        Credentials: the Admin's password is read from FIELDTRACK_ADMIN_PASSWORD and the email from
        FIELDTRACK_ADMIN_EMAIL unless --email is given. A first Admin must have replaced its temporary password.
        """;

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            switch (args)
            {
                case ["seed", .. var rest]:
                    await SeedAsync(Options.Parse(rest), cancellation.Token);
                    return 0;
                case ["run", .. var rest]:
                    await RunAsync(Options.Parse(rest), cancellation.Token);
                    return 0;
                case ["bench", var kind, .. var rest]:
                    return await BenchCli.RunAsync(kind, Options.Parse(rest), cancellation.Token) ? 0 : 3;
                default:
                    Console.WriteLine(Usage);
                    return args.Length == 0 ? 0 : 1;
            }
        }
        catch (SimulatorException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
        catch (HttpRequestException exception)
        {
            await Console.Error.WriteLineAsync($"Cannot reach the API: {exception.Message}");
            return 1;
        }
        catch (JsonException exception)
        {
            await Console.Error.WriteLineAsync($"The API answered with something that is not the expected JSON: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("The API did not answer in time.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }

    private static async Task SeedAsync(Options options, CancellationToken cancellationToken)
    {
        var fleet = FleetFile.Load(options.Require("fleet"));
        using var http = new HttpClient { BaseAddress = ApiAddress(options.Get("api") ?? "http://localhost:8080") };
        var api = await FieldTrackApi.SignInAsync(http, Credential("email", "FIELDTRACK_ADMIN_EMAIL", options), Credential(null, "FIELDTRACK_ADMIN_PASSWORD", options), cancellationToken);
        await Seeder.SeedAsync(api, fleet, Console.Out, cancellationToken);
    }

    private static async Task RunAsync(Options options, CancellationToken cancellationToken)
    {
        var fleet = FleetFile.Load(options.Require("fleet"));
        var run = RunOptionsFrom(options, fleet);

        var pace = run.RatePerSecond is { } rate ? $"{rate} Readings/s" : "as many Readings as the broker acknowledges";
        Console.WriteLine($"Publishing {pace} for {fleet.Devices.Count} Devices to {run.BrokerHost}:{run.BrokerPort}. Ctrl+C stops.");
        var manifest = await FleetRunner.RunAsync(fleet, run, Console.Out, TimeProvider.System, cancellationToken);
        var published = manifest.Devices.Sum(device => device.Ranges.Sum(range => range.LastSequence - range.FirstSequence + 1));
        var injected = manifest.Devices.Sum(device => device.Windows.Count);
        Console.WriteLine($"Published {published} Readings and injected {injected}. Manifest: {Path.GetFullPath(run.ManifestPath)}");
    }

    /// <summary>Reads the options of <c>run</c>, loading its Scenario against <paramref name="fleet"/>.</summary>
    /// <exception cref="SimulatorException">An option is missing, out of range, or its file is unusable.</exception>
    internal static RunOptions RunOptionsFrom(Options options, FleetFile fleet)
    {
        var rate = options.Get("rate") switch
        {
            null => throw new SimulatorException("Missing --rate."),
            "max" => (double?)null,
            _ => options.GetNumber("rate"),
        };
        var port = options.GetNumber("mqtt-port") ?? 1883;
        if (port is not (>= 1 and <= 65535) || port != Math.Floor(port))
        {
            throw new SimulatorException($"--mqtt-port must be a whole number from 1 to 65535, got {port}.");
        }

        var seconds = options.GetNumber("duration");
        if (seconds is not (null or >= 0 and <= MaxDurationSeconds))
        {
            throw new SimulatorException($"--duration must be from 0 to {MaxDurationSeconds} seconds, got {seconds}.");
        }

        return new RunOptions(
            options.Get("mqtt-host") ?? "localhost",
            (int)port,
            rate,
            seconds is { } duration ? TimeSpan.FromSeconds(duration) : null,
            options.Get("state") ?? "simulator-state.json",
            options.Get("manifest") ?? $"manifest-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json",
            options.Get("scenario") is { } scenario ? Scenario.Load(scenario, fleet) : null,
            new RandomRates(PerHour(options, "fault-rate"), PerHour(options, "power-off-rate"), PerHour(options, "coverage-gap-rate")));
    }

    private static double PerHour(Options options, string name) =>
        options.GetNumber(name) is not { } rate ? 0
        : double.IsFinite(rate) && rate >= 0 ? rate
        : throw new SimulatorException($"--{name} must be a number from 0 up (events per Device-hour), got {rate}.");

    /// <summary>The API's base address: https anywhere, plain http only on this machine (the Admin's password and token cross it).</summary>
    internal static Uri ApiAddress(string text) =>
        !Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            ? throw new SimulatorException($"--api must be an absolute http or https URL, got '{text}'.")
        : uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback
            ? throw new SimulatorException($"--api {text} would send the Admin's password in cleartext; use https for a host other than this machine.")
        : uri;

    // The password only comes from the environment: a command-line argument lands in shell history and process lists.
    internal static string Credential(string? option, string variable, Options options) =>
        (option is null ? null : options.Get(option)) ?? Environment.GetEnvironmentVariable(variable)
            ?? throw new SimulatorException($"Set {variable}{(option is null ? string.Empty : $" or pass --{option}")}.");
}
