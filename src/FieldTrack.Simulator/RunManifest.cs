using System.Text.Json;

namespace FieldTrack.Simulator;

/// <summary>Readings one Device published in one Boot: Sequence numbers <paramref name="FirstSequence"/> to <paramref name="LastSequence"/>, both included.</summary>
/// <param name="Boot">The Boot they were published in.</param>
/// <param name="FirstSequence">The first Sequence number published.</param>
/// <param name="LastSequence">The last Sequence number published.</param>
internal sealed record PublishedRange(int Boot, long FirstSequence, long LastSequence);

/// <summary>What one Device published during a run.</summary>
/// <param name="Serial">The Device's serial number.</param>
/// <param name="Ranges">What it published, one range per Boot; empty when nothing reached the broker.</param>
internal sealed record ManifestDevice(string Serial, IReadOnlyList<PublishedRange> Ranges);

/// <summary>
/// The Manifest: the simulator's record of what one run published, the ground truth a benchmark scores stored Readings against
/// (spec #21). Only Readings the broker acknowledged count; one still unacknowledged when the run's grace period ends (broker unreachable) may be stored without being listed.
/// </summary>
/// <param name="StartedAt">When the run started.</param>
/// <param name="EndedAt">When the run stopped.</param>
/// <param name="RatePerSecond">The aggregate rate it aimed for.</param>
/// <param name="Devices">What each Device published, in fleet order.</param>
internal sealed record RunManifest(DateTimeOffset StartedAt, DateTimeOffset EndedAt, double RatePerSecond, IReadOnlyList<ManifestDevice> Devices)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The Manifest of a run that ended with <paramref name="devices"/> in the state they were left in.</summary>
    public static RunManifest Of(DateTimeOffset startedAt, DateTimeOffset endedAt, double ratePerSecond, IEnumerable<SimulatedDevice> devices) =>
        new(startedAt, endedAt, ratePerSecond, [.. devices.Select(device => new ManifestDevice(
            device.Serial,
            device.NextSequence == 0 ? [] : [new PublishedRange(device.Boot, 0, device.NextSequence - 1)]))]);

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
