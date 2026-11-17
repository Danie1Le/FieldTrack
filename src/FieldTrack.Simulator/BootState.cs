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
    /// The file exists but cannot be read. It is not replaced: starting over from 0 would reuse identities.
    /// </exception>
    public static IReadOnlyDictionary<string, int> Raise(string path, IEnumerable<string> serials)
    {
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
            throw new SimulatorException($"The state file {path} is unreadable ({exception.Message}). Fix or delete it; deleting restarts every Device's Boot at 1.", exception);
        }
    }

    // Written to a temporary file first, so a crash mid-write cannot leave a truncated state file.
    private static void Write(string path, Dictionary<string, int> boots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StateFile(boots), _json));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record StateFile(Dictionary<string, int> Boots);
}
