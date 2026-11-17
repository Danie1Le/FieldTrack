using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldTrack.Simulator;

/// <summary>Readings one Device published in one Boot: Sequence numbers <paramref name="FirstSequence"/> to <paramref name="LastSequence"/>, both included.</summary>
/// <param name="Boot">The Boot they were published in.</param>
/// <param name="FirstSequence">The first Sequence number published.</param>
/// <param name="LastSequence">The last Sequence number published.</param>
internal sealed record PublishedRange(int Boot, long FirstSequence, long LastSequence);

/// <summary>
/// One <see cref="Injection"/> as the run applied it: a Fault, Power off, Coverage gap or clock problem in force from
/// <paramref name="Start"/> up to, not including, <paramref name="End"/>. A Reading taken inside the window is affected by it.
/// </summary>
/// <param name="Kind">What it did.</param>
/// <param name="Start">When it started.</param>
/// <param name="End">When it ended; the end of the run when the run ended first.</param>
/// <param name="Amount">How strong it was; the unit depends on <paramref name="Kind"/> (see <see cref="InjectionKind"/>).</param>
internal sealed record ManifestWindow(InjectionKind Kind, DateTimeOffset Start, DateTimeOffset End, double Amount);

/// <summary>What one Device published and suffered during a run.</summary>
/// <param name="Serial">The Device's serial number.</param>
/// <param name="Ranges">What it published, one range per Boot; empty when nothing reached the broker.</param>
/// <param name="Windows">What was injected into it, by start time.</param>
internal sealed record ManifestDevice(string Serial, IReadOnlyList<PublishedRange> Ranges, IReadOnlyList<ManifestWindow> Windows);

/// <summary>
/// The Manifest: the simulator's record of what one run published and injected, the ground truth a benchmark scores stored Readings
/// against (spec #21). Only Readings the broker acknowledged count; one still unacknowledged when the run's grace period ends (broker unreachable) may be stored without being listed.
/// Readings a Coverage gap kept back count once sent; Readings a Power off discarded never do.
/// </summary>
/// <param name="StartedAt">When the run started.</param>
/// <param name="EndedAt">When the run stopped.</param>
/// <param name="RatePerSecond">The aggregate rate it aimed for, or null when it published as fast as the broker acknowledged.</param>
/// <param name="Devices">What each Device published and suffered, in fleet order.</param>
internal sealed record RunManifest(DateTimeOffset StartedAt, DateTimeOffset EndedAt, double? RatePerSecond, IReadOnlyList<ManifestDevice> Devices)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The Manifest of a run that ended with <paramref name="devices"/> in the state they were left in.</summary>
    public static RunManifest Of(DateTimeOffset startedAt, DateTimeOffset endedAt, double? ratePerSecond, IEnumerable<SimulatedDevice> devices)
    {
        var length = endedAt - startedAt;
        return new(startedAt, endedAt, ratePerSecond, [.. devices.Select(device => new ManifestDevice(
            device.Serial,
            device.Ranges,
            // An injection that never started is not in the Manifest; one still running is cut at the end of the run.
            [.. device.Injections.Where(injection => injection.Start < length).OrderBy(injection => injection.Start).Select(injection =>
                new ManifestWindow(injection.Kind, startedAt + injection.Start, startedAt + (injection.End < length ? injection.End : length), injection.Amount))]))]);
    }

    /// <summary>Saves the Manifest as JSON at <paramref name="path"/>, creating its directory.</summary>
    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Written to a temporary file and moved, so a crash mid-write cannot leave a truncated Manifest over a good one.
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(file, this, _json);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
