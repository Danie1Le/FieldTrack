namespace FieldTrack.Simulator;

/// <summary>
/// Paced mode: a target aggregate rate shared by every Device. Each Device publishes on the same interval but starts one
/// slot after the one before, so sends spread evenly instead of ticking together; the ingest spike found synchronised
/// ticks add burst latency (#16).
/// </summary>
internal readonly struct Pacing
{
    // PeriodicTimer takes 1 ms up to about 49.7 days.
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

    /// <summary>The time between one Device's Readings.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(_devices / _ratePerSecond);

    /// <summary>How long after the run starts the Device at <paramref name="deviceIndex"/> publishes its first Reading.</summary>
    public TimeSpan StartOffset(int deviceIndex) => TimeSpan.FromSeconds(deviceIndex / _ratePerSecond);
}
