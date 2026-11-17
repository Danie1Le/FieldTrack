using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldTrack.Simulator;

/// <summary>
/// One simulated Device: it takes the Readings a real Device would, keeps them until the broker acknowledges them and counts
/// how many it did. Sensor values are plausible and steady, with Gaussian noise where the fleet file asks for it, and the Device
/// suffers the <see cref="Injection"/>s of its schedule (Faults, Power offs, Coverage gaps, clock problems). A Power off ends its
/// Boot and starts the next.
/// </summary>
internal sealed class SimulatedDevice
{
    /// <summary>The engine temperature of a healthy Device, in °C.</summary>
    public const double NormalEngineTempC = 85;

    private const double FuelDrainPctPerMinute = 0.5;

    private static readonly TimeSpan _openEndedOverheatRamp = TimeSpan.FromMinutes(4);

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly FleetDevice _spec;
    private readonly IMotion _motion;
    private readonly InjectionSchedule _injections;
    private readonly Func<int> _raiseBoot;
    private readonly Random _random;
    private readonly Queue<byte[]> _outbox = new();
    private readonly List<PublishedRange> _earlierBoots = [];
    private bool _poweredOff;
    private bool _hasCoverage = true;
    private long _captured;
    private double _fuelDrained;
    private TimeSpan _fuelAt;

    /// <summary>
    /// A Device with the traits of <paramref name="spec"/> that is in <paramref name="boot"/>, moves as <paramref name="motion"/> says
    /// and suffers what <paramref name="injections"/> schedules (nothing when null). After a Power off it asks
    /// <paramref name="raiseBoot"/> for its new Boot (the next number when null). Its noise is drawn from the Boot, or from
    /// <paramref name="noiseSeed"/> when given, so a run can be repeated whatever Boot it lands on.
    /// </summary>
    public SimulatedDevice(FleetDevice spec, int boot, IMotion motion, InjectionSchedule? injections = null, Func<int>? raiseBoot = null, int? noiseSeed = null)
    {
        _spec = spec;
        Boot = boot;
        _motion = motion;
        _injections = injections ?? new InjectionSchedule();
        _raiseBoot = raiseBoot ?? (() => Boot + 1);
        _random = new Random(SeedFor($"{spec.Serial}/noise/{noiseSeed ?? boot}"));
    }

    /// <summary>
    /// Starts <paramref name="spec"/> in <paramref name="boot"/>, moving as its fleet entry says: along its route, fixed at its
    /// position, or as a random walk when it has neither. It suffers what <paramref name="scenario"/> scripts for it plus what
    /// <paramref name="rates"/> draws at random, and asks <paramref name="raiseBoot"/> for each new Boot after a Power off. A
    /// <paramref name="noiseSeed"/> replaces the Boot as the seed of the sensor noise.
    /// </summary>
    /// <exception cref="SimulatorException">The Device's route file cannot be read.</exception>
    public static SimulatedDevice Start(FleetDevice spec, int boot, Scenario? scenario = null, RandomRates? rates = null, Func<int>? raiseBoot = null, int? noiseSeed = null)
    {
        IMotion motion = spec switch
        {
            { Route: { } route } => new RouteMotion(GpxRoute.Load(route), spec.SpeedKph),
            { Position: { } position } => new FixedMotion(position),
            // Every walker starts at the same centre and wanders its own way (each seeds from its serial): enough for a demo or a flood.
            _ => new RandomWalkMotion(RandomWalkMotion.DefaultCentre, spec.SpeedKph, RandomWalkMotion.DefaultRadiusMeters, new Random(SeedFor(spec.Serial))),
        };
        // Seeded from the Boot too, so each run (which raises the Boot) draws differently while a replay of the same state file draws the same.
        var random = rates is { Any: true } ? new RandomInjections(rates, spec.Sensors, new Random(SeedFor($"{spec.Serial}/injections/{boot}"))) : null;
        return new SimulatedDevice(spec, boot, motion, new InjectionSchedule(random, scenario?.InjectionsFor(spec.Serial)), raiseBoot, noiseSeed);
    }

    /// <summary>The Device's serial number.</summary>
    public string Serial => _spec.Serial;

    /// <summary>The MQTT topic the Device publishes to: <c>fieldtrack/v1/devices/{serial}/telemetry</c> (spec #21).</summary>
    public string Topic => TopicFor(Serial);

    /// <summary>The telemetry topic of the Device with <paramref name="serial"/>.</summary>
    public static string TopicFor(string serial) => $"fieldtrack/v1/devices/{serial}/telemetry";

    /// <summary>The Boot this Device is in.</summary>
    public int Boot { get; private set; }

    /// <summary>How many Readings the broker acknowledged in this Boot (the last one, while powered off); they are Sequence numbers 0 to one less.</summary>
    public long PublishedCount { get; private set; }

    /// <summary>What the broker acknowledged, one range per Boot; a Boot that published nothing has none.</summary>
    public IReadOnlyList<PublishedRange> Ranges => [.. _earlierBoots, .. Range(Boot, PublishedCount)];

    /// <summary>What was injected into the Device so far, scripted or drawn, in the order it was scheduled.</summary>
    public IReadOnlyList<Injection> Injections => _injections.All;

    /// <summary>How far into the run the Device last stepped, or null before its first step; it acts on nothing later.</summary>
    public TimeSpan? LastStep { get; private set; }

    /// <summary>
    /// Advances the Device to <paramref name="elapsed"/> into the run, <paramref name="now"/> by the wall clock: it takes a Reading
    /// unless a Power off has it switched off, and starts its new Boot when a Power off ends. A Power off that began and ended
    /// between two steps still costs the Boot.
    /// </summary>
    public void Step(DateTimeOffset now, TimeSpan elapsed)
    {
        _injections.Advance(elapsed);
        var off = _injections.Active(InjectionLane.Connection, elapsed) is { Kind: InjectionKind.PowerOff };
        if (off || _injections.All.Any(injection => injection.Kind == InjectionKind.PowerOff && injection.OverlapWith(LastStep ?? TimeSpan.Zero, elapsed) > TimeSpan.Zero))
        {
            PowerOff();
        }

        LastStep = elapsed;
        if (off)
        {
            return;
        }

        if (_poweredOff)
        {
            PowerOn();
        }

        _outbox.Enqueue(Build(now, elapsed));
        _hasCoverage = _injections.Active(InjectionLane.Connection, elapsed) is not { Kind: InjectionKind.CoverageGap };
    }

    /// <summary>
    /// The payload to send next, or null when nothing waits. Until <see cref="Published"/> every call returns the same bytes,
    /// so a retry sends the Reading again and ingest sees an identical duplicate, not a conflicting one (ADR 0001, ADR 0003).
    /// </summary>
    public byte[]? NextToSend => _hasCoverage && _outbox.TryPeek(out var payload) ? payload : null;

    /// <summary>Records that the broker acknowledged the Reading <see cref="NextToSend"/> returned.</summary>
    public void Published()
    {
        _outbox.Dequeue();
        PublishedCount++;
    }

    private static IEnumerable<PublishedRange> Range(int boot, long published) => published == 0 ? [] : [new PublishedRange(boot, 0, published - 1)];

    // A Power off keeps nothing: what was taken but not acknowledged is lost with the Boot.
    private void PowerOff()
    {
        if (_poweredOff)
        {
            return;
        }

        _outbox.Clear();
        _poweredOff = true;
    }

    // Every Boot starts its Sequence numbers at 0 (ADR 0001). The ended Boot's range is archived here, where it stops being the
    // current one, so Ranges lists it exactly once however the run ends.
    private void PowerOn()
    {
        _earlierBoots.AddRange(Range(Boot, PublishedCount));
        Boot = _raiseBoot();
        PublishedCount = 0;
        _captured = 0;
        _poweredOff = false;
    }

    // Seeded from the serial, so a Device wanders the same way every run; string.GetHashCode is randomised per process.
    private static int SeedFor(string key) => key.Aggregate(17, (hash, c) => (hash * 31) + c);

    // Drains half a percent a minute (a leak multiplies that while it lasts), then tops up when it gets low, so a long run never ends on an empty tank.
    private double FuelPct(TimeSpan elapsed)
    {
        var leaked = _injections.All
            .Where(injection => injection.Kind == InjectionKind.FuelLeak)
            .Sum(leak => leak.OverlapWith(_fuelAt, elapsed).TotalMinutes * (leak.Amount - 1));
        _fuelDrained += FuelDrainPctPerMinute * ((elapsed - _fuelAt).TotalMinutes + leaked);
        _fuelAt = elapsed;
        return 90 - (_fuelDrained % 75);
    }

    // An Overheat Fault spends its first 40% ramping from the normal temperature to its peak, the next 30% at the peak, the last 30% cooling.
    private double EngineTempC(TimeSpan elapsed)
    {
        if (_injections.Active(InjectionLane.Overheat, elapsed) is not { } overheat)
        {
            return NormalEngineTempC;
        }

        // One that lasts to the end of the run has no length to take shares of: it ramps as a 10-minute one does, then holds its peak.
        if (overheat.End == TimeSpan.MaxValue)
        {
            return NormalEngineTempC + ((overheat.Amount - NormalEngineTempC) * Math.Min(1, (elapsed - overheat.Start) / _openEndedOverheatRamp));
        }

        var progress = (elapsed - overheat.Start) / (overheat.End - overheat.Start);
        var level = progress switch { < 0.4 => progress / 0.4, < 0.7 => 1, _ => (1 - progress) / 0.3 };
        return NormalEngineTempC + ((overheat.Amount - NormalEngineTempC) * level);
    }

    // A clock fault shifts the Device's own time, never the moment the broker receives the Reading.
    private TimeSpan ClockError(TimeSpan elapsed) => _injections.Active(InjectionLane.Clock, elapsed) switch
    {
        { Kind: InjectionKind.ClockDrift } drift => TimeSpan.FromSeconds(drift.Amount / 1_000_000 * (elapsed - drift.Start).TotalSeconds),
        { } step => TimeSpan.FromSeconds(step.Amount),
        _ => TimeSpan.Zero,
    };

    private byte[] Build(DateTimeOffset now, TimeSpan elapsed)
    {
        var motion = _motion.At(elapsed);
        var message = new Message(
            Boot, _captured++, (now + ClockError(elapsed)).ToUnixTimeMilliseconds(), Math.Round(motion.Position.Lat, 6), Math.Round(motion.Position.Lon, 6),
            Value(Sensor.SpeedKph, motion.SpeedKph),
            Value(Sensor.EngineTempC, EngineTempC(elapsed)),
            Value(Sensor.FuelPct, FuelPct(elapsed)),
            Value(Sensor.VibrationMmS, 2),
            Value(Sensor.BatteryV, 13.8));
        return JsonSerializer.SerializeToUtf8Bytes(message, _json);
    }

    private float? Value(Sensor sensor, double value) => _spec.Sensors.Contains(sensor) ? (float)Math.Round(WithNoise(sensor, value), 2) : null;

    // Box-Muller: a standard normal draw from two uniform ones (1 - u keeps the logarithm finite).
    private double WithNoise(Sensor sensor, double value)
    {
        if (_spec.Noise is not { } noise || !noise.TryGetValue(sensor, out var sigma))
        {
            return value;
        }

        var gaussian = Math.Sqrt(-2 * Math.Log(1 - _random.NextDouble())) * Math.Cos(2 * Math.PI * _random.NextDouble());
        var noisy = value + (sigma * gaussian);
        // Noise must not push a sensor past what it can read: no negative speed, no fuel above a full tank.
        return sensor == Sensor.FuelPct ? Math.Clamp(noisy, 0, 100) : Math.Max(noisy, 0);
    }

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
