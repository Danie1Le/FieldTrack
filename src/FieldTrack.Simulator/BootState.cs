using System.Text.Json;

namespace FieldTrack.Simulator;

/// <summary>
/// The local state file holding each Device's Boot counter. Every simulator start raises each Device's Boot by one,
/// so a (Device, Boot, Sequence number) identity is never published twice (ADR 0001) however often the simulator restarts.
/// </summary>
internal static class BootState
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Raises the Boot counter of every Device in <paramref name="serials"/> by one, saves the file, and returns the
    /// new counts. A Device not seen before gets Boot 1; Devices not in <paramref name="serials"/> keep their count.
    /// </summary>
    /// <exception cref="SimulatorException">
    /// The file exists but cannot be read (it is not replaced: starting over from 0 would reuse identities), or another
    /// simulator is raising it at the same moment.
    /// </exception>
    public static IReadOnlyDictionary<string, int> Raise(string path, IEnumerable<string> serials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var exclusive = Lock(path);
        var state = Read(path);
        var raised = serials.ToDictionary(serial => serial, serial => state.GetValueOrDefault(serial) + 1);
        foreach (var (serial, boot) in raised)
        {
            state[serial] = boot;
        }

        Write(path, state);
        return raised;
    }

    private static Dictionary<string, int> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), _json)?.Boots ?? throw new JsonException("no \"boots\" object");
        }
        catch (JsonException exception)
        {
            throw new SimulatorException(
                $"The state file {path} is unreadable ({exception.Message}). Fix it, or delete it and reset the database too: "
                + "deleting restarts every Device's Boot at 1, and Readings already stored under those Boots make the new ones duplicates.",
                exception);
        }
    }

    // Two simulators raising one file at once would both read Boot n and publish the same identities; the second one stops instead.
    // The lock file stays behind: deleting it on close would let a third simulator lock a fresh file while a second holds the old one.
    private static FileStream Lock(string path)
    {
        try
        {
            return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new SimulatorException($"Another simulator is raising the Boots in {path} right now; start this one again, or give it its own state file.", exception);
        }
    }

    // Written to a temporary file and flushed to disk first, so neither a crash nor a power loss can leave a truncated state file.
    private static void Write(string path, Dictionary<string, int> boots)
    {
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(file, new StateFile(boots), _json);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed record StateFile(Dictionary<string, int> Boots);
}
