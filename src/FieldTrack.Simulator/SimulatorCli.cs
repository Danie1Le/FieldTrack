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

          run --fleet <file> --rate <readings per second> [--duration <seconds>] [--mqtt-host localhost] [--mqtt-port 1883]
              [--state simulator-state.json] [--manifest manifest-<time>.json]
              Publishes Readings for every Device at the target aggregate rate, spread evenly across Devices, until
              --duration passes or Ctrl+C. Each start raises every Device's Boot (kept in --state) and restarts its
              Sequence numbers at 0. The Manifest lists what was published. MQTT is plain TCP without credentials:
              point it only at a local or trusted-network broker.

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
        var rate = options.GetNumber("rate") ?? throw new SimulatorException("Missing --rate.");
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

        var run = new RunOptions(
            options.Get("mqtt-host") ?? "localhost",
            (int)port,
            rate,
            seconds is { } duration ? TimeSpan.FromSeconds(duration) : null,
            options.Get("state") ?? "simulator-state.json",
            options.Get("manifest") ?? $"manifest-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json");

        Console.WriteLine($"Publishing {rate} Readings/s for {fleet.Devices.Count} Devices to {run.BrokerHost}:{run.BrokerPort}. Ctrl+C stops.");
        var manifest = await FleetRunner.RunAsync(fleet, run, Console.Out, TimeProvider.System, cancellationToken);
        var published = manifest.Devices.Sum(device => device.Ranges.Sum(range => range.LastSequence - range.FirstSequence + 1));
        Console.WriteLine($"Published {published} Readings. Manifest: {Path.GetFullPath(run.ManifestPath)}");
    }

    /// <summary>The API's base address: https anywhere, plain http only on this machine (the Admin's password and token cross it).</summary>
    internal static Uri ApiAddress(string text) =>
        !Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            ? throw new SimulatorException($"--api must be an absolute http or https URL, got '{text}'.")
        : uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback
            ? throw new SimulatorException($"--api {text} would send the Admin's password in cleartext; use https for a host other than this machine.")
        : uri;

    // The password only comes from the environment: a command-line argument lands in shell history and process lists.
    private static string Credential(string? option, string variable, Options options) =>
        (option is null ? null : options.Get(option)) ?? Environment.GetEnvironmentVariable(variable)
            ?? throw new SimulatorException($"Set {variable}{(option is null ? string.Empty : $" or pass --{option}")}.");
}
