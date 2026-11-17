namespace FieldTrack.Simulator.Bench;

/// <summary>One synthetic Reading of a seeded history: where the Device was and what its sensors read at its Device time.</summary>
/// <param name="Sequence">Sequence number within the track.</param>
/// <param name="DeviceTime">When the Device took it.</param>
/// <param name="Position">Where it was.</param>
/// <param name="SpeedKph">Speed in km/h.</param>
/// <param name="EngineTempC">Engine temperature in degrees Celsius.</param>
/// <param name="FuelPct">Fuel level in percent.</param>
/// <param name="VibrationMmS">Vibration in mm/s.</param>
/// <param name="BatteryV">Battery voltage in volts.</param>
internal readonly record struct HistoryReading(
    long Sequence, DateTimeOffset DeviceTime, Position Position, float SpeedKph, float EngineTempC, float FuelPct, float VibrationMmS, float BatteryV);

/// <summary>
/// The synthetic track of one Device for the query benchmarks (#46): it drives along a <see cref="GpxRoute"/> at a steady speed and
/// reports at a fixed interval. The positions get a little GPS jitter, so Readings from many loops of the same route do not pile up on
/// identical points, which a real fleet never produces and which would flatter a spatial index.
/// </summary>
internal static class HistoryTrack
{
    /// <summary>The most a reported position strays from the route, in metres.</summary>
    public const double JitterMeters = 5;

    /// <summary>
    /// The Readings of a Device that starts <paramref name="startMeters"/> along <paramref name="route"/> at <paramref name="from"/>, one per
    /// <paramref name="interval"/> while Device time is before <paramref name="to"/>, drawing jitter and sensor values from <paramref name="random"/>.
    /// </summary>
    public static IEnumerable<HistoryReading> Generate(
        GpxRoute route, double speedKph, double startMeters, DateTimeOffset from, DateTimeOffset to, TimeSpan interval, Random random)
    {
        for (long sequence = 0; from + (interval * sequence) < to; sequence++)
        {
            var time = from + (interval * sequence);
            var onRoute = route.PositionAt(startMeters + (speedKph / 3.6 * (time - from).TotalSeconds));
            var position = Geo.Offset(onRoute, random.NextDouble() * 2 * Math.PI, random.NextDouble() * JitterMeters);
            yield return new HistoryReading(
                sequence, time, position, (float)(speedKph + (random.NextDouble() * 4) - 2), 70 + (random.NextSingle() * 25),
                20 + (random.NextSingle() * 80), 1 + (random.NextSingle() * 4), 12 + (random.NextSingle() * 2.4f));
        }
    }
}
