using System.Text.Json;

namespace FieldTrack.Simulator;

/// <summary>
/// The simulator's command line. It drives demos and benchmarks: <c>seed</c> prepares the platform from a fleet file
/// (spec #21, Simulator and bench).
/// </summary>
internal static class SimulatorCli
{
    private const string Usage = """
        FieldTrack simulator

          seed --fleet <file> [--api http://localhost:8080] [--email <admin email>]
              Creates the fleet's Assets, Devices and Attachments through the REST API as Admin. Safe to repeat.

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
