using System.Text.Json;

namespace FieldTrack.Simulator;

/// <summary>
/// A Scenario: the script of a repeatable run, listing which Device suffers which <see cref="Injection"/> and when, so a demo or
/// a benchmark can inject the same Faults, Power offs, Coverage gaps and clock problems every time (spec #21). Times count in
/// seconds from the start of the run.
/// </summary>
internal sealed class Scenario
{
    // TimeSpan.FromSeconds and CancellationTokenSource.CancelAfter take far more, but a run never lasts that long.
    private const double MaxSeconds = 4_000_000;
    private const double MaxClockAmount = 1_000_000_000;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { AllowDuplicateProperties = false };

    private readonly Dictionary<string, List<Injection>> _injections;

    private Scenario(Dictionary<string, List<Injection>> injections) => _injections = injections;

    /// <summary>The Scenario with nothing in it: the run injects only what random injection draws.</summary>
    public static Scenario None { get; } = new([]);

    /// <summary>What happens to the Device with <paramref name="serial"/>, in the order the file lists it.</summary>
    public IReadOnlyList<Injection> InjectionsFor(string serial) => _injections.GetValueOrDefault(serial) ?? [];

    /// <summary>Reads and validates the Scenario file at <paramref name="path"/> against the Devices of <paramref name="fleet"/>.</summary>
    /// <exception cref="SimulatorException">The file is missing or breaks a rule.</exception>
    public static Scenario Load(string path, FleetFile fleet) =>
        File.Exists(path) ? Parse(File.ReadAllText(path), fleet) : throw new SimulatorException($"Scenario file not found: {path}");

    /// <summary>Validates <paramref name="json"/> as a Scenario for the Devices of <paramref name="fleet"/>.</summary>
    /// <exception cref="SimulatorException">The Scenario is not valid JSON of the expected shape or breaks a rule.</exception>
    public static Scenario Parse(string json, FleetFile fleet)
    {
        ScenarioDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ScenarioDto>(json, _json);
        }
        catch (JsonException exception)
        {
            throw new SimulatorException($"The Scenario is not valid JSON of the expected shape: {exception.Message}", exception);
        }

        if (dto?.Injections is not { Count: > 0 } entries)
        {
            throw new SimulatorException("The Scenario lists no injections.");
        }

        var schedules = new Dictionary<string, InjectionSchedule>();
        for (var i = 0; i < entries.Count; i++)
        {
            var (device, injection) = ToInjection(entries[i], $"Scenario injection {i + 1}", fleet);
            if (!schedules.TryGetValue(device.Serial, out var schedule))
            {
                schedules[device.Serial] = schedule = new InjectionSchedule();
            }

            if (!schedule.TryAdd(injection))
            {
                throw new SimulatorException($"Scenario injection {i + 1}: {injection.Kind} on {device.Serial} overlaps another injection of the same lane.");
            }
        }

        return new Scenario(schedules.ToDictionary(pair => pair.Key, pair => pair.Value.All.ToList()));
    }

    private static (FleetDevice Device, Injection Injection) ToInjection(InjectionDto? dto, string where, FleetFile fleet)
    {
        if (dto?.Device is not { } serial)
        {
            throw new SimulatorException($"{where} has no device.");
        }

        var device = fleet.Devices.FirstOrDefault(candidate => candidate.Serial == serial)
            ?? throw new SimulatorException($"{where}: device {serial} is not in the fleet.");
        var kind = Enum.GetNames<InjectionKind>().FirstOrDefault(name => string.Equals(name, dto.Kind, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<InjectionKind>(name)
            : throw new SimulatorException($"{where}: unknown kind '{dto.Kind}'; use {string.Join(", ", Enum.GetNames<InjectionKind>().Select(JsonNamingPolicy.CamelCase.ConvertName))}.");
        var isClock = kind is InjectionKind.ClockOffset or InjectionKind.ClockDrift or InjectionKind.ClockJump;

        if (dto.AtSeconds is not (>= 0 and <= MaxSeconds))
        {
            throw new SimulatorException($"{where}: atSeconds must be from 0 to {MaxSeconds}.");
        }

        // A clock problem with no duration lasts to the end of the run.
        if (dto.DurationSeconds is not (null or > 0 and <= MaxSeconds) || (dto.DurationSeconds is null && !isClock))
        {
            throw new SimulatorException($"{where}: durationSeconds must be above 0 and at most {MaxSeconds}{(isClock ? string.Empty : " and is required")}.");
        }

        RequireSensor(kind, device, where);
        var start = TimeSpan.FromSeconds(dto.AtSeconds.Value);
        var end = dto.DurationSeconds is { } seconds ? start + TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
        return (device, new Injection(kind, start, end, AmountOf(kind, dto.Amount, where)));
    }

    private static void RequireSensor(InjectionKind kind, FleetDevice device, string where)
    {
        var needed = kind switch
        {
            InjectionKind.Overheat => Sensor.EngineTempC,
            InjectionKind.FuelLeak => Sensor.FuelPct,
            _ => (Sensor?)null,
        };
        if (needed is { } sensor && !device.Sensors.Contains(sensor))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(sensor.ToString());
            throw new SimulatorException($"{where}: {device.Serial} does not carry {name}, so a {kind} on it would change nothing.");
        }
    }

    private static double AmountOf(InjectionKind kind, double? amount, string where)
    {
        switch (kind)
        {
            case InjectionKind.Overheat:
                var peak = amount ?? Injection.DefaultOverheatPeakC;
                return double.IsFinite(peak) && peak > SimulatedDevice.NormalEngineTempC
                    ? peak
                    : throw new SimulatorException($"{where}: amount, the peak in °C, must be above the normal {SimulatedDevice.NormalEngineTempC}.");
            case InjectionKind.FuelLeak:
                var factor = amount ?? Injection.DefaultFuelLeakFactor;
                return double.IsFinite(factor) && factor > 1
                    ? factor
                    : throw new SimulatorException($"{where}: amount, the drain factor, must be above 1.");
            case InjectionKind.PowerOff or InjectionKind.CoverageGap:
                return 0;
            default:
                return amount is { } value && Math.Abs(value) <= MaxClockAmount
                    ? value
                    : throw new SimulatorException($"{where}: a {kind} needs an amount (seconds, or ppm for a drift) of at most {MaxClockAmount} either way.");
        }
    }

    private sealed record ScenarioDto(List<InjectionDto?>? Injections);

    private sealed record InjectionDto(string? Device, string? Kind, double? AtSeconds, double? DurationSeconds, double? Amount);
}
