using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldTrack.Simulator;

/// <summary>
/// One simulated Device in one Boot: it builds the payloads a real Device would publish and counts how many reached
/// the broker. Sensor values are plausible and steady; noise and Faults come with Scenarios (#43).
/// </summary>
internal sealed class SimulatedDevice
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly FleetDevice _spec;
    private readonly IMotion _motion;

    /// <summary>A Device with the traits of <paramref name="spec"/> that is in <paramref name="boot"/> and moves as <paramref name="motion"/> says.</summary>
    public SimulatedDevice(FleetDevice spec, int boot, IMotion motion)
    {
        _spec = spec;
        Boot = boot;
        _motion = motion;
    }

    /// <summary>
    /// Starts <paramref name="spec"/> in <paramref name="boot"/>, moving as its fleet entry says: along its route, fixed at its
    /// position, or as a random walk when it has neither.
    /// </summary>
    /// <exception cref="SimulatorException">The Device's route file cannot be read.</exception>
    public static SimulatedDevice Start(FleetDevice spec, int boot) => new(spec, boot, spec switch
    {
        { Route: { } route } => new RouteMotion(GpxRoute.Load(route), spec.SpeedKph),
        { Position: { } position } => new FixedMotion(position),
        // Seeded from the serial, so a Device wanders the same way every run.
        _ => new RandomWalkMotion(RandomWalkMotion.DefaultCentre, spec.SpeedKph, 1500, new Random(spec.Serial.Aggregate(17, (hash, c) => (hash * 31) + c))),
    });

    /// <summary>The Device's serial number.</summary>
    public string Serial => _spec.Serial;

    /// <summary>The MQTT topic the Device publishes to: <c>fieldtrack/v1/devices/{serial}/telemetry</c> (spec #21).</summary>
    public string Topic => $"fieldtrack/v1/devices/{Serial}/telemetry";

    /// <summary>The Boot this Device is in.</summary>
    public int Boot { get; }

    /// <summary>The Sequence number of the next Reading; also how many Readings were published in this Boot.</summary>
    public long NextSequence { get; private set; }

    /// <summary>
    /// The payload of the next Reading as it is at <paramref name="now"/>, <paramref name="elapsed"/> into the run. It
    /// stays the same Sequence number until <see cref="Published"/>, so a retry sends the Reading again, not a new one.
    /// </summary>
    public byte[] NextPayload(DateTimeOffset now, TimeSpan elapsed)
    {
        var motion = _motion.At(elapsed);
        var message = new Message(
            Boot, NextSequence, now.ToUnixTimeMilliseconds(), Math.Round(motion.Position.Lat, 6), Math.Round(motion.Position.Lon, 6),
            Value(Sensor.SpeedKph, motion.SpeedKph),
            Value(Sensor.EngineTempC, 85),
            Value(Sensor.FuelPct, FuelPct(elapsed)),
            Value(Sensor.VibrationMmS, 2),
            Value(Sensor.BatteryV, 13.8));
        return JsonSerializer.SerializeToUtf8Bytes(message, _json);
    }

    /// <summary>Records that the broker acknowledged the Reading <see cref="NextPayload"/> built.</summary>
    public void Published() => NextSequence++;

    // Drains half a percent a minute, then tops up when it gets low, so a long run never ends on an empty tank.
    private static double FuelPct(TimeSpan elapsed) => 90 - ((elapsed.TotalMinutes * 0.5) % 75);

    private float? Value(Sensor sensor, double value) => _spec.Sensors.Contains(sensor) ? (float)Math.Round(value, 2) : null;

    private sealed record Message(
        int Boot,
        long Seq,
        long Ts,
        double Lat,
        double Lon,
        float? SpeedKph,
        float? EngineTempC,
        float? FuelPct,
        float? VibrationMmS,
        float? BatteryV);
}
