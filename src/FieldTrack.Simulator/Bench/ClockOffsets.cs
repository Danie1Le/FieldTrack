namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The database clock's offset from this machine's, measured before and after a run. Docker Desktop's VM clock can drift (or jump after
/// the host sleeps) over a ten-minute run, so latency is corrected by the mean and a run whose two offsets disagree is flagged.
/// </summary>
/// <param name="Before">Database clock minus this machine's, before the run.</param>
/// <param name="After">The same, after publishing ended.</param>
internal sealed record ClockOffsets(TimeSpan Before, TimeSpan After)
{
    // A few milliseconds of NTP-style measurement noise is normal on Docker Desktop (0 to 25 ms between probes); more means the clocks moved.
    private static readonly TimeSpan _stable = TimeSpan.FromMilliseconds(10);

    /// <summary>What latency is corrected by.</summary>
    public TimeSpan Mean => (Before + After) / 2;

    /// <summary>How far the two measurements differ.</summary>
    public TimeSpan Drift => (After - Before).Duration();

    /// <summary>Whether the clocks moved relative to each other during the run, so the correction is doubtful.</summary>
    public bool Drifted => Drift > _stable;
}
