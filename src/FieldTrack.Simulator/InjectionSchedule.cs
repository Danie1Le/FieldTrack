namespace FieldTrack.Simulator;

/// <summary>Every <see cref="Injection"/> of one Device in one run: the ones a Scenario scripted and the ones random injection drew.</summary>
internal sealed class InjectionSchedule
{
    private readonly List<Injection> _injections = [];

    /// <summary>The Injections in the order they were added.</summary>
    public IReadOnlyList<Injection> All => _injections;

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
