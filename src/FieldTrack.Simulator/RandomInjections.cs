namespace FieldTrack.Simulator;

/// <summary>How often random injection starts each kind of <see cref="Injection"/>, per Device-hour.</summary>
/// <param name="FaultsPerHour">Overheat or fuel-leak Faults.</param>
/// <param name="PowerOffsPerHour">Power offs.</param>
/// <param name="CoverageGapsPerHour">Coverage gaps.</param>
internal sealed record RandomRates(double FaultsPerHour, double PowerOffsPerHour, double CoverageGapsPerHour)
{
    /// <summary>No random injection at all: only a Scenario injects.</summary>
    public static RandomRates None { get; } = new(0, 0, 0);

    /// <summary>Whether any kind has a rate above zero.</summary>
    public bool Any => FaultsPerHour > 0 || PowerOffsPerHour > 0 || CoverageGapsPerHour > 0;
}

/// <summary>
/// Draws the random Faults, Power offs and Coverage gaps of one Device. Each tick of the Device is one independent draw per kind,
/// with probability rate x time since the previous tick, so a rate means the same however fast the Device publishes.
/// </summary>
internal sealed class RandomInjections(RandomRates rates, IReadOnlyList<Sensor> sensors, Random random)
{
    private static readonly TimeSpan _faultDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _powerOffDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _coverageGapDuration = TimeSpan.FromMinutes(2);

    /// <summary>The Injections that start at <paramref name="to"/>, the end of the interval that began at <paramref name="from"/>.</summary>
    public IReadOnlyList<Injection> Draw(TimeSpan from, TimeSpan to)
    {
        var hours = (to - from).TotalHours;
        var drawn = new List<Injection>();
        if (Happens(rates.FaultsPerHour, hours) && FaultKinds() is { Count: > 0 } kinds)
        {
            var kind = kinds[random.Next(kinds.Count)];
            drawn.Add(new Injection(kind, to, to + _faultDuration, kind == InjectionKind.Overheat ? Injection.DefaultOverheatPeakC : Injection.DefaultFuelLeakFactor));
        }

        if (Happens(rates.PowerOffsPerHour, hours))
        {
            drawn.Add(new Injection(InjectionKind.PowerOff, to, to + _powerOffDuration, 0));
        }

        if (Happens(rates.CoverageGapsPerHour, hours))
        {
            drawn.Add(new Injection(InjectionKind.CoverageGap, to, to + _coverageGapDuration, 0));
        }

        return drawn;
    }

    private bool Happens(double perHour, double hours) => perHour > 0 && random.NextDouble() < perHour * hours;

    // A Fault on a sensor the Device does not carry would change nothing it could report.
    private List<InjectionKind> FaultKinds()
    {
        var kinds = new List<InjectionKind>();
        if (sensors.Contains(Sensor.EngineTempC))
        {
            kinds.Add(InjectionKind.Overheat);
        }

        if (sensors.Contains(Sensor.FuelPct))
        {
            kinds.Add(InjectionKind.FuelLeak);
        }

        return kinds;
    }
}
