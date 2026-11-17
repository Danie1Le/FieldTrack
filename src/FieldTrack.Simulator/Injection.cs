namespace FieldTrack.Simulator;

/// <summary>What an <see cref="Injection"/> does to its Device.</summary>
internal enum InjectionKind
{
    /// <summary>Fault: engine temperature ramps to a peak, holds, then cools. <see cref="Injection.Amount"/> is the peak in °C.</summary>
    Overheat,

    /// <summary>Fault: fuel drains faster. <see cref="Injection.Amount"/> is the factor on the normal drain rate.</summary>
    FuelLeak,

    /// <summary>Power off: the Device sends and keeps nothing, then starts a new Boot.</summary>
    PowerOff,

    /// <summary>Coverage gap: the Device keeps taking Readings but cannot send them until the gap ends.</summary>
    CoverageGap,

    /// <summary>Clock fault: the Device's clock is wrong by a fixed <see cref="Injection.Amount"/> seconds.</summary>
    ClockOffset,

    /// <summary>Clock fault: the Device's clock gains <see cref="Injection.Amount"/> parts per million, so its error grows steadily.</summary>
    ClockDrift,

    /// <summary>Clock fault: the Device's clock suddenly steps by <see cref="Injection.Amount"/> seconds and stays there.</summary>
    ClockJump,
}

/// <summary>Injections of one lane cannot overlap, because a Device can only be in one such state at a time.</summary>
internal enum InjectionLane
{
    /// <summary>The engine temperature.</summary>
    Overheat,

    /// <summary>The fuel level.</summary>
    FuelLeak,

    /// <summary>Whether the Device is on and connected: Power offs and Coverage gaps.</summary>
    Connection,

    /// <summary>The Device's clock.</summary>
    Clock,
}

/// <summary>
/// One Fault, Power off, Coverage gap or clock problem applied to one Device for a window of the run. The same record is what a
/// Scenario scripts, what random injection draws, and what the Manifest reports. A Device acts on an Injection only when it takes a
/// Reading: a window shorter than the time between two Readings can pass between them and change nothing but a Power off's Boot.
/// </summary>
/// <param name="Kind">What it does.</param>
/// <param name="Start">When it starts, counted from the start of the run.</param>
/// <param name="End">When it ends, counted from the start of the run; <see cref="TimeSpan.MaxValue"/> when it lasts to the end of the run.</param>
/// <param name="Amount">How strong it is; the unit depends on <paramref name="Kind"/>. Unused by Power offs and Coverage gaps.</param>
internal sealed record Injection(InjectionKind Kind, TimeSpan Start, TimeSpan End, double Amount)
{
    /// <summary>The peak of an Overheat when a Scenario gives none, in °C: well past a typical alert threshold.</summary>
    public const double DefaultOverheatPeakC = 120;

    /// <summary>The drain factor of a fuel leak when a Scenario gives none.</summary>
    public const double DefaultFuelLeakFactor = 10;

    /// <summary>The state of the Device this Injection controls.</summary>
    public InjectionLane Lane => Kind switch
    {
        InjectionKind.Overheat => InjectionLane.Overheat,
        InjectionKind.FuelLeak => InjectionLane.FuelLeak,
        InjectionKind.PowerOff or InjectionKind.CoverageGap => InjectionLane.Connection,
        _ => InjectionLane.Clock,
    };

    /// <summary>Whether the Injection is in force <paramref name="elapsed"/> into the run: from its start up to, not including, its end.</summary>
    public bool Covers(TimeSpan elapsed) => elapsed >= Start && elapsed < End;

    /// <summary>How much of the span from <paramref name="from"/> to <paramref name="to"/> the Injection is in force.</summary>
    public TimeSpan OverlapWith(TimeSpan from, TimeSpan to)
    {
        var start = Start > from ? Start : from;
        var end = End < to ? End : to;
        return end > start ? end - start : TimeSpan.Zero;
    }

    /// <summary>Whether the two share a lane and a moment.</summary>
    public bool Clashes(Injection other) => Lane == other.Lane && Start < other.End && other.Start < End;
}
