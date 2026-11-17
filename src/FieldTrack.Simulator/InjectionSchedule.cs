namespace FieldTrack.Simulator;

/// <summary>Every <see cref="Injection"/> of one Device in one run: the ones a Scenario scripted and the ones random injection drew.</summary>
internal sealed class InjectionSchedule(RandomInjections? random = null)
{
    private readonly List<Injection> _injections = [];
    private TimeSpan _drawnUntil;

    /// <summary>The Injections in the order they were added.</summary>
    public IReadOnlyList<Injection> All => _injections;

    /// <summary>
    /// Lets random injection draw what starts by <paramref name="elapsed"/>. A draw that clashes with one already scheduled is
    /// dropped: a Device cannot be in two states of one lane at once, and the Manifest must show exactly what happened.
    /// </summary>
    public void Advance(TimeSpan elapsed)
    {
        foreach (var injection in random?.Draw(_drawnUntil, elapsed) ?? [])
        {
            TryAdd(injection);
        }

        _drawnUntil = elapsed;
    }

    /// <summary>Adds <paramref name="injection"/> unless it clashes with one already scheduled.</summary>
    /// <returns><see langword="false"/> when it clashes, and nothing is added.</returns>
    public bool TryAdd(Injection injection)
    {
        if (_injections.Any(injection.Clashes))
        {
            return false;
        }

        _injections.Add(injection);
        return true;
    }

    /// <summary>The Injection of <paramref name="lane"/> in force <paramref name="elapsed"/> into the run, or null.</summary>
    public Injection? Active(InjectionLane lane, TimeSpan elapsed) =>
        _injections.FirstOrDefault(injection => injection.Lane == lane && injection.Covers(elapsed));
}
