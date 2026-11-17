using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class GpxRouteTests
{
    // About 1.11 km due north, then about 1.11 km due east at the equator, so lengths are easy to check by hand.
    private const string LShape = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
          <trk><trkseg>
            <trkpt lat="0.00" lon="0.00"/>
            <trkpt lat="0.01" lon="0.00"/>
            <trkpt lat="0.01" lon="0.01"/>
          </trkseg></trk>
        </gpx>
        """;

    [Fact]
    public void The_route_length_is_the_distance_along_its_points()
    {
        var route = GpxRoute.Parse(LShape, "l.gpx");

        route.LengthMeters.ShouldBe(2223.9, tolerance: 1);
    }

    [Fact]
    public void A_distance_along_the_route_maps_to_a_position_between_its_points()
    {
        var route = GpxRoute.Parse(LShape, "l.gpx");

        route.PositionAt(0).ShouldBe(new Position(0, 0));
        var halfwayNorth = route.PositionAt(route.LengthMeters / 4);
        halfwayNorth.Lat.ShouldBe(0.005, tolerance: 1e-6);
        halfwayNorth.Lon.ShouldBe(0, tolerance: 1e-9);
        var eastLeg = route.PositionAt(route.LengthMeters * 0.75);
        eastLeg.Lat.ShouldBe(0.01, tolerance: 1e-6);
        eastLeg.Lon.ShouldBe(0.005, tolerance: 1e-6);
    }

    [Fact]
    public void Going_past_the_end_starts_the_route_again()
    {
        var route = GpxRoute.Parse(LShape, "l.gpx");

        var again = route.PositionAt(route.LengthMeters + route.LengthMeters / 4);

        again.Lat.ShouldBe(0.005, tolerance: 1e-6);
        again.Lon.ShouldBe(0, tolerance: 1e-9);
    }

    [Fact]
    public void Route_points_and_track_points_both_count_and_the_namespace_is_optional()
    {
        var route = GpxRoute.Parse("""<gpx><rte><rtept lat="1" lon="2"/><rtept lat="1.001" lon="2"/></rte></gpx>""", "r.gpx");

        route.PositionAt(0).ShouldBe(new Position(1, 2));
    }

    [Theory]
    [InlineData("""<gpx><trk><trkseg><trkpt lat="1" lon="2"/></trkseg></trk></gpx>""", "at least two points")]
    [InlineData("""<gpx><trk><trkseg><trkpt lat="1" lon="2"/><trkpt lat="1" lon="2"/></trkseg></trk></gpx>""", "no length")]
    [InlineData("""<gpx><trk><trkseg><trkpt lat="1" lon="2"/><trkpt lat="x" lon="2"/></trkseg></trk></gpx>""", "lat and lon")]
    [InlineData("""<gpx><trk><trkseg><trkpt lat="1" lon="2"/><trkpt lat="95" lon="2"/></trkseg></trk></gpx>""", "lat and lon")]
    [InlineData("""<gpx><trk><trkseg><trkpt lat="1" lon="2"/><trkpt lon="2"/></trkseg></trk></gpx>""", "lat and lon")]
    [InlineData("not xml", "not valid XML")]
    [InlineData("""<!DOCTYPE gpx [<!ENTITY a "1">]><gpx><trk><trkseg><trkpt lat="&a;" lon="2"/><trkpt lat="2" lon="2"/></trkseg></trk></gpx>""", "not valid XML")]
    public void A_route_that_cannot_be_followed_is_refused_with_the_reason(string gpx, string reason)
    {
        var exception = Should.Throw<SimulatorException>(() => GpxRoute.Parse(gpx, "bad.gpx"));

        exception.Message.ShouldContain("bad.gpx");
        exception.Message.ShouldContain(reason);
    }

    [Fact]
    public void A_missing_route_file_is_refused_with_its_path()
    {
        var exception = Should.Throw<SimulatorException>(() => GpxRoute.Load("no-such-route.gpx"));

        exception.Message.ShouldContain("no-such-route.gpx");
    }

    [Fact]
    public void Every_committed_route_can_be_followed_and_is_a_closed_loop()
    {
        var routes = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fleets", "routes"), "*.gpx");

        routes.ShouldNotBeEmpty();
        foreach (var path in routes)
        {
            var route = GpxRoute.Load(path);
            route.LengthMeters.ShouldBeGreaterThan(500, path);
            // An open route makes its Device jump from the last point to the first on every lap.
            Geo.Distance(route.PositionAt(0), route.PositionAt(route.LengthMeters - 0.001)).ShouldBeLessThan(50, path);
        }
    }
}
