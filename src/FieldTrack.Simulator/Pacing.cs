using System.Runtime.CompilerServices;

namespace FieldTrack.Simulator;

/// <summary>One Reading a Device owes: when the schedule made it due, and how many older ones were given up to reach it.</summary>
/// <param name="Due">Time from the run's start at which the Reading was due. It only counts the Readings owed; the Device takes the Reading when it wakes.</param>
/// <param name="Skipped">Readings dropped just before this one because the Device was more than <see cref="Pacing.MaxDebt"/> behind; 0 when it was not.</param>
internal readonly record struct OwedReading(TimeSpan Due, long Skipped);

/// <summary>
/// Paced mode: a target aggregate rate shared by every Device. Each Device publishes on the same interval but starts one
/// slot after the one before, so sends spread evenly instead of ticking together; the ingest spike found synchronised
/// ticks add burst latency (#16). Readings are scheduled by elapsed time, not by a repeating timer: a timer wakes up late by
/// the OS granularity (about 15.6 ms on Windows) and every late wake-up would lower the rate, so a Device that wakes late
/// owes the Readings it missed (#105). The schedule decides only how many are owed: each Reading is taken, and stamped, when
/// the Device takes it, so a late wake-up adds nothing to the ingest latency the benchmarks measure (SPEC section 8).
/// </summary>
internal readonly struct Pacing
{
    /// <summary>The most a Device may fall behind. Older Readings are skipped, so a broker outage ends in a short catch-up and not a burst of old Readings.</summary>
    public static readonly TimeSpan MaxDebt = TimeSpan.FromSeconds(1);

    // One connection that waits for each acknowledgement cannot reach more than 1,000 Readings per second, so a faster
    // rate only leaves the Device ever further behind; Task.Delay takes up to about 49.7 days.
    private const double MinIntervalSeconds = 0.001;
    private const double MaxIntervalSeconds = 4_000_000;

    private readonly int _devices;
    private readonly double _ratePerSecond;

    /// <summary>Paces <paramref name="devices"/> Devices to <paramref name="ratePerSecond"/> Readings per second in total.</summary>
    /// <exception cref="SimulatorException">
    /// There is no Device, the rate is not a positive number, or it gives each Device a Reading interval outside 1 ms to 4,000,000 s.
    /// </exception>
    public Pacing(int devices, double ratePerSecond)
    {
        if (devices <= 0 || !double.IsFinite(ratePerSecond) || ratePerSecond <= 0)
        {
            throw new SimulatorException("Paced mode needs at least one Device and a rate above 0 Readings per second.");
        }

        (_devices, _ratePerSecond) = (devices, ratePerSecond);
        if (devices / ratePerSecond is < MinIntervalSeconds or > MaxIntervalSeconds)
        {
            throw new SimulatorException(
                $"--rate {ratePerSecond} gives each of the {devices} Devices a Reading every {devices / ratePerSecond:G3} s; it must be 1 ms to {MaxIntervalSeconds} s.");
        }
    }

    /// <summary>
    /// How long after the run starts the Device at <paramref name="deviceIndex"/> owes its <paramref name="count"/>th Reading (from 0).
    /// Reading <c>deviceIndex + count * devices</c> of the whole fleet is due at that number over the rate, so the fleet owes exactly
    /// the rate however late its Devices wake.
    /// </summary>
    public TimeSpan DueAt(int deviceIndex, long count) => TimeSpan.FromSeconds((deviceIndex + (count * _devices)) / _ratePerSecond);

    /// <summary>
    /// The Readings the Device at <paramref name="deviceIndex"/> owes, each returned once <paramref name="time"/> has reached its due
    /// time since <paramref name="startTimestamp"/>. A Device that is late (a coarse timer, a slow broker) gets the overdue ones back
    /// to back, so it catches up instead of lowering the rate; one more than <see cref="MaxDebt"/> behind skips the oldest
    /// (<see cref="OwedReading.Skipped"/>), so it does not flood the broker. Ends only by cancellation.
    /// </summary>
    public async IAsyncEnumerable<OwedReading> OwedAsync(
        int deviceIndex, TimeProvider time, long startTimestamp, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (long count = 0; ; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = time.GetElapsedTime(startTimestamp);
            var oldestKept = (long)Math.Ceiling((((elapsed - MaxDebt).TotalSeconds * _ratePerSecond) - deviceIndex) / _devices);
            var skipped = Math.Max(0, oldestKept - count);
            count += skipped;
            var due = DueAt(deviceIndex, count);
            var wait = due - elapsed;
            if (wait > TimeSpan.Zero)
            {
                // Task.Delay counts whole milliseconds and a fraction rounds down, which would wake the Device early.
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)), time, cancellationToken);
            }

            yield return new OwedReading(due, skipped);
        }
    }
}
