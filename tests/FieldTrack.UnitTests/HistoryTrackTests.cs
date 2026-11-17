using FieldTrack.Simulator;
using FieldTrack.Simulator.Bench;

namespace FieldTrack.UnitTests;

public sealed class HistoryTrackTests
{
    // About 1.11 km due north, then about 1.11 km due east.
    private const string LShape = """
        <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
          <trk><trkseg>
            <trkpt lat="0.00" lon="0.00"/>
            <trkpt lat="0.01" lon="0.00"/>
            <trkpt lat="0.01" lon="0.01"/>
          </trkseg></trk>
        </gpx>
        """;

    private static readonly DateTimeOffset _from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_track_has_one_Reading_per_interval_with_consecutive_sequence_numbers()
    {
        var route = GpxRoute.Parse(LShape, "l.gpx");

        var readings = HistoryTrack.Generate(route, 30, 0, _from, _from.AddDays(3), TimeSpan.FromMinutes(8), new Random(1)).ToList();

        readings.Count.ShouldBe(540);
        readings.Select(reading => reading.Sequence).ShouldBe(Enumerable.Range(0, 540).Select(i => (long)i));
        readings.Select(reading => reading.DeviceTime).ShouldBe(Enumerable.Range(0, 540).Select(i => _from.AddMinutes(8 * i)));
    }

    [Fact]
    public void Every_position_lies_within_the_GPS_jitter_of_the_route()
    {
        var route = GpxRoute.Parse(LShape, "l.gpx");
        // The route sampled every metre is the independent reference: a position is on the route if one sample is near it.
        var samples = Enumerable.Range(0, (int)route.LengthMeters + 1).Select(meters => route.PositionAt(meters)).ToList();

        var readings = HistoryTrack.Generate(route, 40, 700, _from, _from.AddDays(2), TimeSpan.FromMinutes(8), new Random(2)).ToList();

        foreach (var reading in readings)
        {
            samples.Min(sample => Geo.Distance(sample, reading.Position)).ShouldBeLessThanOrEqualTo(HistoryTrack.JitterMeters + 1);
        }

        readings.Select(reading => reading.Position).Distinct().Count().ShouldBeGreaterThan(readings.Count / 2, "jitter spreads the points; a loop would otherwise repeat them");
    }

    [Fact]
    public void The_same_seed_gives_the_same_track()
    {
        var route = GpxRoute.Parse(LShape, "l.gpx");

        var first = HistoryTrack.Generate(route, 30, 0, _from, _from.AddDays(1), TimeSpan.FromMinutes(30), new Random(3)).ToList();
        var second = HistoryTrack.Generate(route, 30, 0, _from, _from.AddDays(1), TimeSpan.FromMinutes(30), new Random(3)).ToList();

        second.ShouldBe(first);
    }
}
