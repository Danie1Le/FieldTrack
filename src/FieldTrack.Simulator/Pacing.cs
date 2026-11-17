using System.Runtime.CompilerServices;

namespace FieldTrack.Simulator;

/// <summary>
/// Paced mode: a target aggregate rate shared by every Device. Each Device publishes on the same interval but starts one
/// slot after the one before, so sends spread evenly instead of ticking together; the ingest spike found synchronised
/// ticks add burst latency (#16). Readings are scheduled by elapsed time, not by a repeating timer: a timer wakes up late by
/// the OS granularity (about 15.6 ms on Windows) and every late wake-up would lower the rate, so a Device that wakes late
/// publishes the Readings it owes (#105).
/// </summary>
internal readonly struct Pacing
{
    // A Device waits for each acknowledgement, so it cannot keep Readings closer than 1 ms; Task.Delay takes up to about 49.7 days.
    private const double MinIntervalSeconds = 0.001;
    private const double MaxIntervalSeconds = 4_000_000;

    private readonly int _devices;
    private readonly double _ratePerSecond;

    /// <summary>Paces <paramref name="devices"/> Devices to <paramref name="ratePerSecond"/> Readings per second in total.</summary>
    /// <exception cref="SimulatorException">
    /// There is no Device, the rate is not a positive number, or it gives each Device an interval a timer cannot keep.
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
    /// The times, from the run's start, at which the Device at <paramref name="deviceIndex"/> owes a Reading, each returned once
    /// <paramref name="time"/> has reached it since <paramref name="startTimestamp"/>. A Device that is late (a coarse timer, a slow
    /// broker) gets the overdue ones back to back, so it catches up instead of dropping them. Ends only by cancellation.
    /// </summary>
    public async IAsyncEnumerable<TimeSpan> DueTimesAsync(
        int deviceIndex, TimeProvider time, long startTimestamp, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (long count = 0; ; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var due = DueAt(deviceIndex, count);
            var wait = due - time.GetElapsedTime(startTimestamp);
            if (wait > TimeSpan.Zero)
            {
                // Task.Delay counts whole milliseconds and a fraction rounds down, which would wake the Device early.
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)), time, cancellationToken);
            }

            yield return due;
        }
    }
}
