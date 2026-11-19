using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldTrack.Simulator.Bench;

/// <summary>A finished benchmark: where its result file is, what it says, and whether it met its pass bar.</summary>
/// <param name="Path">The result file.</param>
/// <param name="Json">The result file's content.</param>
/// <param name="Pass">Whether the run met its bar.</param>
internal sealed record BenchOutcome(string Path, string Json, bool Pass);

/// <summary>The running platform a benchmark measures, and where it keeps what it writes.</summary>
/// <param name="BrokerHost">Host of the MQTT broker the Devices publish to.</param>
/// <param name="BrokerPort">Port of the MQTT broker.</param>
/// <param name="LocalDatabase">The platform's database, read to count and time what ingest stored; null when the platform is remote.</param>
/// <param name="StatePath">The Boot counter file; shared with <c>run</c>, so a bench never reuses a Reading key a demo run published.</param>
/// <param name="OutputDirectory">Where result JSON and Manifests go (<c>bench/results</c>).</param>
/// <param name="LocalComposeProject">The docker compose project of the stack, to read image tags and limits from and to find the ingest container in; null when the platform is remote.</param>
internal sealed record BenchTarget(string BrokerHost, int BrokerPort, BenchDatabase? LocalDatabase, string StatePath, string OutputDirectory, string? LocalComposeProject) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Whether the platform runs elsewhere (the Azure demo): there is no database or compose project to read, and the result records no local stack.</summary>
    public bool Remote => LocalDatabase is null;

    /// <summary>The platform's database; the benchmarks that read it are refused before they run against a remote platform.</summary>
    public BenchDatabase Database => LocalDatabase ?? throw new InvalidOperationException("A remote target has no database.");

    /// <summary>The compose project of the local stack.</summary>
    public string ComposeProject => LocalComposeProject ?? throw new InvalidOperationException("A remote target has no compose project.");

    /// <summary>Releases the database connections of a local target.</summary>
    public ValueTask DisposeAsync() => LocalDatabase is null ? ValueTask.CompletedTask : LocalDatabase.DataSource.DisposeAsync();

    /// <summary>A fresh path in <see cref="OutputDirectory"/> for a file of this <paramref name="bench"/> run, stamped with <paramref name="time"/>.</summary>
    public string PathFor(string prefix, string bench, DateTimeOffset time, string extension = "json") =>
        Path.Combine(OutputDirectory, $"{prefix}{bench}-{time:yyyyMMddTHHmmssZ}.{extension}");

    /// <summary>
    /// Writes the result of a <paramref name="bench"/> run next to its Manifest: what was asked (<paramref name="parameters"/>), what was
    /// measured (<paramref name="results"/>), whether it met its pass bar, and the environment it ran in.
    /// </summary>
    public async Task<BenchOutcome> WriteResultAsync(
        string bench, DateTimeOffset startedAt, object parameters, object results, bool pass, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            new { bench, startedAt, pass, parameters, results, environment = await BenchEnvironment.CaptureAsync(LocalComposeProject, LocalDatabase, cancellationToken) }, _json);
        Directory.CreateDirectory(OutputDirectory);
        var path = PathFor(string.Empty, bench, startedAt);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return new BenchOutcome(path, json, pass);
    }
}
