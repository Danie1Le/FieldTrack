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
    private readonly Queue<byte[]> _outbox = new();
    private long _captured;

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
        // Every walker starts at the same centre; fine for the demo's one, worth an offset when Scenarios (#43) add more.
        _ => new RandomWalkMotion(RandomWalkMotion.DefaultCentre, spec.SpeedKph, RandomWalkMotion.DefaultRadiusMeters, new Random(SeedFor(spec.Serial))),
    });

    /// <summary>The Device's serial number.</summary>
    public string Serial => _spec.Serial;

    /// <summary>The MQTT topic the Device publishes to: <c>fieldtrack/v1/devices/{serial}/telemetry</c> (spec #21).</summary>
    public string Topic => $"fieldtrack/v1/devices/{Serial}/telemetry";

    /// <summary>The Boot this Device is in.</summary>
    public int Boot { get; }

    /// <summary>How many Readings the broker acknowledged in this Boot; they are Sequence numbers 0 to one less.</summary>
    public long PublishedCount { get; private set; }

    /// <summary>What the broker acknowledged, one range per Boot; a Boot that published nothing has none.</summary>
    public IReadOnlyList<PublishedRange> Ranges => PublishedCount == 0 ? [] : [new PublishedRange(Boot, 0, PublishedCount - 1)];

    /// <summary>The Device takes a Reading at <paramref name="now"/>, <paramref name="elapsed"/> into the run.</summary>
    public void Step(DateTimeOffset now, TimeSpan elapsed) => _outbox.Enqueue(Build(now, elapsed));

    /// <summary>
    /// The payload to send next, or null when nothing waits. Until <see cref="Published"/> every call returns the same bytes,
    /// so a retry sends the Reading again and ingest sees an identical duplicate, not a conflicting one (ADR 0001, ADR 0003).
    /// </summary>
    public byte[]? NextToSend => _outbox.TryPeek(out var payload) ? payload : null;

    /// <summary>Records that the broker acknowledged the Reading <see cref="NextToSend"/> returned.</summary>
    public void Published()
    {
        _outbox.Dequeue();
        PublishedCount++;
    }

    // Seeded from the serial, so a Device wanders the same way every run; string.GetHashCode is randomised per process.
    private static int SeedFor(string serial) => serial.Aggregate(17, (hash, c) => (hash * 31) + c);

    // Drains half a percent a minute, then tops up when it gets low, so a long run never ends on an empty tank.
    private static double FuelPct(TimeSpan elapsed) => 90 - ((elapsed.TotalMinutes * 0.5) % 75);

    private byte[] Build(DateTimeOffset now, TimeSpan elapsed)
    {
        var motion = _motion.At(elapsed);
        var message = new Message(
            Boot, _captured++, now.ToUnixTimeMilliseconds(), Math.Round(motion.Position.Lat, 6), Math.Round(motion.Position.Lon, 6),
            Value(Sensor.SpeedKph, motion.SpeedKph),
            Value(Sensor.EngineTempC, 85),
            Value(Sensor.FuelPct, FuelPct(elapsed)),
            Value(Sensor.VibrationMmS, 2),
            Value(Sensor.BatteryV, 13.8));
        return JsonSerializer.SerializeToUtf8Bytes(message, _json);
    }

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
