namespace FieldTrack.Simulator;

/// <summary>
/// The simulator's command line. It drives demos and benchmarks: <c>seed</c> prepares the platform from a fleet file
/// and <c>run</c> publishes Readings for it (spec #21, Simulator and bench).
/// </summary>
internal static class SimulatorCli
{
    private const string Usage = """
        FieldTrack simulator

          seed --fleet <file> [--api http://localhost:8080] [--email <admin email>]
              Creates the fleet's Assets, Devices and Attachments through the REST API as Admin. Safe to repeat.

          run --fleet <file> --rate <readings per second> [--duration <seconds>] [--mqtt-host localhost] [--mqtt-port 1883]
              [--state simulator-state.json] [--manifest manifest-<time>.json]
              Publishes Readings for every Device at the target aggregate rate, spread evenly across Devices, until
              --duration passes or Ctrl+C. Each start raises every Device's Boot (kept in --state) and restarts its
              Sequence numbers at 0. The Manifest lists what was published.

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
        catch (OperationCanceledException)
        {
            return 1;
        }
    }

    private static async Task SeedAsync(Options options, CancellationToken cancellationToken)
    {
        var fleet = FleetFile.Load(options.Require("fleet"));
        using var http = new HttpClient { BaseAddress = new Uri(options.Get("api") ?? "http://localhost:8080") };
        var api = await FieldTrackApi.SignInAsync(http, Credential("email", "FIELDTRACK_ADMIN_EMAIL", options), Credential(null, "FIELDTRACK_ADMIN_PASSWORD", options), cancellationToken);
        await Seeder.SeedAsync(api, fleet, Console.Out, cancellationToken);
    }

    private static async Task RunAsync(Options options, CancellationToken cancellationToken)
    {
        var fleet = FleetFile.Load(options.Require("fleet"));
        var rate = options.GetNumber("rate") ?? throw new SimulatorException("Missing --rate.");
        var run = new RunOptions(
            options.Get("mqtt-host") ?? "localhost",
            (int)(options.GetNumber("mqtt-port") ?? 1883),
            rate,
            options.GetNumber("duration") is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            options.Get("state") ?? "simulator-state.json",
            options.Get("manifest") ?? $"manifest-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json");

        Console.WriteLine($"Publishing {rate} Readings/s for {fleet.Devices.Count} Devices to {run.BrokerHost}:{run.BrokerPort}. Ctrl+C stops.");
        var manifest = await FleetRunner.RunAsync(fleet, run, Console.Out, TimeProvider.System, cancellationToken);
        var published = manifest.Devices.Sum(device => device.Ranges.Sum(range => range.LastSequence - range.FirstSequence + 1));
        Console.WriteLine($"Published {published} Readings. Manifest: {Path.GetFullPath(run.ManifestPath)}");
    }

    // The password only comes from the environment: a command-line argument lands in shell history and process lists.
    private static string Credential(string? option, string variable, Options options) =>
        (option is null ? null : options.Get(option)) ?? Environment.GetEnvironmentVariable(variable)
            ?? throw new SimulatorException($"Set {variable}{(option is null ? string.Empty : $" or pass --{option}")}.");
}
