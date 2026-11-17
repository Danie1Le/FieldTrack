namespace FieldTrack.Simulator.Bench;

/// <summary>When the crash benchmark kills the ingest container.</summary>
internal static class CrashSchedule
{
    // The ingest container needs about this long to restart, reconnect and be hit again meaningfully.
    private static readonly TimeSpan _settle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _minimumSlot = TimeSpan.FromSeconds(40);

    /// <summary>
    /// <paramref name="kills"/> moments at random within a run of <paramref name="duration"/>: the run minus <see cref="_settle"/> at each
    /// end is cut into equal slots with one kill in each slot's middle half, so kills are random but never bunch up (a kill during a
    /// restart would test nothing). Offsets come from the run's start, in order.
    /// </summary>
    /// <exception cref="SimulatorException">The run is too short to give each kill a slot of at least 40 s.</exception>
    public static IReadOnlyList<TimeSpan> Plan(int kills, TimeSpan duration, Random random)
    {
        var slot = (duration - (2 * _settle)) / kills;
        if (slot < _minimumSlot)
        {
            throw new SimulatorException($"{kills} kills need a run of at least {(kills * _minimumSlot) + (2 * _settle)}; --duration is {duration}.");
        }

        return [.. Enumerable.Range(0, kills).Select(i => _settle + (slot * i) + (slot * (0.25 + (0.5 * random.NextDouble()))))];
    }
}
