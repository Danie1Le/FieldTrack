using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class MotionTests
{
    [Fact]
    public void A_stationary_Device_reports_its_position_and_no_speed()
    {
        var motion = new FixedMotion(new Position(45.5, -122.6));

        var sample = motion.At(TimeSpan.FromHours(3));

        sample.Position.ShouldBe(new Position(45.5, -122.6));
        sample.SpeedKph.ShouldBe(0);
    }

    [Fact]
    public void A_Device_on_a_route_covers_speed_times_time_along_it()
    {
        var route = GpxRoute.Parse("""<gpx><trk><trkseg><trkpt lat="0" lon="0"/><trkpt lat="0.1" lon="0"/></trkseg></trk></gpx>""", "line.gpx");
        var motion = new RouteMotion(route, speedKph: 36);

        var sample = motion.At(TimeSpan.FromMinutes(1));

        // 36 km/h is 10 m/s, so 600 m north: 600 / 111,195 m per degree.
        sample.Position.Lat.ShouldBe(600 / 111195.0, tolerance: 1e-5);
        sample.SpeedKph.ShouldBe(36);
    }

    [Fact]
    public void A_random_walk_stays_near_its_centre_and_never_stands_still()
    {
        var centre = new Position(45.5, -122.6);
        var motion = new RandomWalkMotion(centre, speedKph: 30, radiusMeters: 500, new Random(1));
        var previous = centre;
        var farthest = 0.0;

        for (var second = 1; second <= 3600; second += 5)
        {
            var sample = motion.At(TimeSpan.FromSeconds(second));
            farthest = Math.Max(farthest, Geo.Distance(centre, sample.Position));
            Geo.Distance(previous, sample.Position).ShouldBeGreaterThan(0);
            previous = sample.Position;
        }

        // One step (5 s at 30 km/h is about 42 m) may carry it past the radius before it turns back.
        farthest.ShouldBeLessThan(500 + 50);
        farthest.ShouldBeGreaterThan(100);
    }

    [Fact]
    public void A_random_walk_with_the_same_seed_repeats_exactly()
    {
        var centre = new Position(45.5, -122.6);
        var one = new RandomWalkMotion(centre, 30, 500, new Random(7));
        var two = new RandomWalkMotion(centre, 30, 500, new Random(7));

        for (var second = 1; second <= 60; second++)
        {
            one.At(TimeSpan.FromSeconds(second)).Position.ShouldBe(two.At(TimeSpan.FromSeconds(second)).Position);
        }
    }
}
