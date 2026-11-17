using System.Text.Json;
using System.Text.RegularExpressions;
using FieldTrack.Domain.Assets;

namespace FieldTrack.Simulator;

/// <summary>A fixed point on the map in WGS 84 degrees.</summary>
/// <param name="Lat">Latitude, -90 to 90.</param>
/// <param name="Lon">Longitude, -180 to 180.</param>
internal sealed record Position(double Lat, double Lon);

/// <summary>One simulated Device and the Asset it is attached to.</summary>
/// <param name="Serial">The serial number the Device publishes under.</param>
/// <param name="AssetName">The name of the Asset the Device is attached to; the serial number when the file gives none.</param>
/// <param name="AssetType">The kind of equipment the Asset is.</param>
/// <param name="Route">Path of the GPX route a mobile Device follows, or null.</param>
/// <param name="Position">Where a stationary Device reports from, or null.</param>
/// <param name="Sensors">The sensors the Device carries.</param>
internal sealed record FleetDevice(
    string Serial,
    string AssetName,
    AssetType AssetType,
    string? Route,
    Position? Position,
    IReadOnlyList<Sensor> Sensors);

/// <summary>
/// The fleet file: every simulated Device, so a demo and a benchmark start from one setup (spec #21). A Device with a <c>route</c> follows it, one with a <c>position</c> reports from there,
/// and one with neither random-walks.
/// </summary>
internal sealed partial class FleetFile
{
    private const int MaxAssetNameLength = 200;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { AllowDuplicateProperties = false };

    private FleetFile(IReadOnlyList<FleetDevice> devices) => Devices = devices;

    /// <summary>The Devices in file order.</summary>
    public IReadOnlyList<FleetDevice> Devices { get; }

    /// <summary>Reads and validates the fleet file at <paramref name="path"/>; route paths are relative to the file.</summary>
    public static FleetFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new SimulatorException($"Fleet file not found: {path}");
        }

        return Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>Validates <paramref name="json"/> as a fleet file whose route paths are relative to <paramref name="baseDirectory"/>.</summary>
    public static FleetFile Parse(string json, string baseDirectory)
    {
        FleetDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FleetDto>(json, _json);
        }
        catch (JsonException exception)
        {
            throw new SimulatorException($"The fleet file is not valid JSON of the expected shape: {exception.Message}", exception);
        }

        if (dto?.Devices is not { Count: > 0 } devices)
        {
            throw new SimulatorException("The fleet file lists no devices.");
        }

        var parsed = devices.Select(device => ToDevice(device, baseDirectory)).ToList();
        foreach (var twice in parsed.GroupBy(device => device.Serial).Where(group => group.Count() > 1))
        {
            throw new SimulatorException($"Serial number {twice.Key} appears twice in the fleet file.");
        }

        // The API allows one Device per Asset, so a shared Asset would fail halfway through seeding.
        foreach (var shared in parsed.GroupBy(device => device.AssetName).Where(group => group.Count() > 1))
        {
            throw new SimulatorException($"Devices {string.Join(" and ", shared.Select(device => device.Serial))} both use Asset '{shared.Key}'.");
        }

        return new FleetFile(parsed);
    }

    private static FleetDevice ToDevice(DeviceDto? dto, string baseDirectory)
    {
        // The same rule as the API (one MQTT topic level), so a bad serial fails here and not halfway through seeding.
        var serial = dto?.Serial ?? throw new SimulatorException("A device in the fleet file has no serial.");
        if (!SerialPattern().IsMatch(serial))
        {
            throw new SimulatorException($"Serial '{serial}' must be 1 to 64 letters, digits, '.', '_' or '-'.");
        }

        // The API's rule for an Asset name, checked here for the same reason.
        if (dto.AssetName is not null && (string.IsNullOrWhiteSpace(dto.AssetName) || dto.AssetName.Length > MaxAssetNameLength))
        {
            throw new SimulatorException($"Device {serial}: assetName must be 1 to {MaxAssetNameLength} characters and not blank.");
        }

        if (!Enum.TryParse<AssetType>(dto.AssetType, ignoreCase: true, out var assetType) || !Enum.IsDefined(assetType))
        {
            throw new SimulatorException($"Device {serial}: assetType must be one of {string.Join(", ", Enum.GetNames<AssetType>())}.");
        }

        if (dto.Route is not null && dto.Position is not null)
        {
            throw new SimulatorException($"Device {serial}: give a route or a position, not both.");
        }

        // A missing coordinate is null, which fails the range check, instead of 0.
        if (dto.Position is { Lat: not (>= -90 and <= 90) } or { Lon: not (>= -180 and <= 180) })
        {
            throw new SimulatorException($"Device {serial}: position must have lat within -90..90 and lon within -180..180.");
        }

        return new FleetDevice(
            serial,
            dto.AssetName ?? serial,
            assetType,
            dto.Route is null ? null : Path.GetFullPath(dto.Route, baseDirectory),
            dto.Position is { Lat: { } lat, Lon: { } lon } ? new Position(lat, lon) : null,
            (dto.Sensors ?? []).Select(name => ParseSensor(serial, name)).Distinct().ToList());
    }

    private static Sensor ParseSensor(string serial, string name) =>
        Enum.TryParse<Sensor>(name, ignoreCase: true, out var sensor) && Enum.IsDefined(sensor)
            ? sensor
            : throw new SimulatorException($"Device {serial}: unknown sensor '{name}'; use {string.Join(", ", Enum.GetNames<Sensor>())}.");

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SerialPattern();

    private sealed record FleetDto(List<DeviceDto?>? Devices);

    private sealed record DeviceDto(string? Serial, string? AssetName, string? AssetType, string? Route, PositionDto? Position, List<string>? Sensors);

    private sealed record PositionDto(double? Lat, double? Lon);
}
