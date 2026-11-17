namespace FieldTrack.Simulator;

/// <summary>Where a simulated Device is and how fast it is going.</summary>
/// <param name="Position">Its position.</param>
/// <param name="SpeedKph">Its speed in km/h.</param>
internal readonly record struct MotionSample(Position Position, double SpeedKph);

/// <summary>How one simulated Device moves, as a function of the time since the run started.</summary>
internal interface IMotion
{
    /// <summary>The Device's state after <paramref name="elapsed"/> since the run started. Calls come with rising times.</summary>
    MotionSample At(TimeSpan elapsed);
}

/// <summary>A stationary Device, such as a generator or a substation sensor, reporting one position.</summary>
internal sealed class FixedMotion(Position position) : IMotion
{
    /// <inheritdoc />
    public MotionSample At(TimeSpan elapsed) => new(position, 0);
}

/// <summary>A mobile Device looping along a <see cref="GpxRoute"/> at a steady speed.</summary>
internal sealed class RouteMotion(GpxRoute route, double speedKph) : IMotion
{
    /// <inheritdoc />
    public MotionSample At(TimeSpan elapsed) => new(route.PositionAt(speedKph / 3.6 * elapsed.TotalSeconds), speedKph);
}

/// <summary>
/// The fallback for a mobile Device with no route: a wandering heading at a steady speed that turns back
/// to the centre once the Device passes <c>radiusMeters</c>, so it stays near where the map is looking.
/// </summary>
internal sealed class RandomWalkMotion : IMotion
{
    /// <summary>Where random walkers wander when the fleet file gives no better idea (Portland, the demo site).</summary>
    public static readonly Position DefaultCentre = new(45.5231, -122.6765);

    // Radians of heading change per step at most; small enough that tracks look like vehicles, not dust.
    private const double MaxTurn = 0.4;

    private readonly Position _centre;
    private readonly double _speedKph;
    private readonly double _radiusMeters;
    private readonly Random _random;
    private Position _position;
    private double _heading;
    private TimeSpan _last = TimeSpan.Zero;

    /// <summary>A walker starting at <paramref name="centre"/>, drawing every turn from <paramref name="random"/>.</summary>
    public RandomWalkMotion(Position centre, double speedKph, double radiusMeters, Random random)
    {
        (_centre, _position, _speedKph, _radiusMeters, _random) = (centre, centre, speedKph, radiusMeters, random);
        _heading = random.NextDouble() * 2 * Math.PI;
    }

    /// <inheritdoc />
    public MotionSample At(TimeSpan elapsed)
    {
        var seconds = (elapsed - _last).TotalSeconds;
        _last = elapsed;
        _heading = Geo.Distance(_centre, _position) > _radiusMeters
            ? Geo.Bearing(_position, _centre)
            : _heading + ((_random.NextDouble() - 0.5) * 2 * MaxTurn);
        _position = Geo.Offset(_position, _heading, _speedKph / 3.6 * seconds);
        return new MotionSample(_position, _speedKph);
    }
}
