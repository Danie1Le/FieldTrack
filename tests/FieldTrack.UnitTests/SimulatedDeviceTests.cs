using System.Text.Json;
using FieldTrack.Domain.Assets;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class SimulatedDeviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static SimulatedDevice Truck(int boot = 3, params Sensor[] sensors) =>
        new(new FleetDevice("TRK-001", "Haul truck 1", AssetType.Truck, null, new Position(45.5, -122.6), 30, sensors), boot, new FixedMotion(new Position(45.5, -122.6)));

    [Fact]
    public void A_payload_is_a_Reading_by_the_telemetry_contract()
    {
        var device = Truck(boot: 3, [.. Enum.GetValues<Sensor>()]);

        var payload = device.NextPayload(Now, TimeSpan.FromMinutes(10));

        TelemetryPayload.TryParse(payload, Now, out var reading, out _).ShouldBeTrue();
        reading.Boot.ShouldBe(3);
        reading.SequenceNumber.ShouldBe(0);
        reading.DeviceTime.ShouldBe(Now);
        reading.Latitude.ShouldBe(45.5);
        reading.Longitude.ShouldBe(-122.6);
        reading.SpeedKph.ShouldBe(0);
        reading.EngineTempC.ShouldNotBeNull();
        reading.FuelPct.ShouldNotBeNull();
        reading.VibrationMmS.ShouldNotBeNull();
        reading.BatteryV.ShouldNotBeNull();
    }

    [Fact]
    public void A_payload_carries_only_the_sensors_the_Device_has()
    {
        var device = Truck(sensors: [Sensor.FuelPct, Sensor.BatteryV]);

        using var document = JsonDocument.Parse(device.NextPayload(Now, TimeSpan.Zero));

        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Order().ShouldBe(["batteryV", "boot", "fuelPct", "lat", "lon", "seq", "ts"]);
    }

    [Fact]
    public void The_Sequence_number_moves_on_only_when_a_Reading_was_published()
    {
        var device = Truck();

        var first = device.NextPayload(Now, TimeSpan.Zero);
        var retried = device.NextPayload(Now, TimeSpan.Zero);
        device.Published();
        var next = device.NextPayload(Now.AddSeconds(1), TimeSpan.FromSeconds(1));

        retried.ShouldBe(first);
        Sequence(first).ShouldBe(0);
        Sequence(next).ShouldBe(1);
        device.NextSequence.ShouldBe(1);
    }

    [Fact]
    public void Fuel_drains_over_time_and_starts_again_when_it_runs_low()
    {
        var device = Truck(sensors: [Sensor.FuelPct]);

        var fuel = Enumerable.Range(0, 400).Select(minute => Fuel(device.NextPayload(Now, TimeSpan.FromMinutes(minute)))).ToArray();

        fuel[0].ShouldBeGreaterThan(fuel[60]);
        fuel.Min().ShouldBeGreaterThanOrEqualTo(10);
        fuel.Max().ShouldBeLessThanOrEqualTo(100);
        fuel.Zip(fuel.Skip(1)).ShouldContain(pair => pair.Second > pair.First);
    }

    [Fact]
    public void A_Device_with_a_route_starts_on_it_one_with_a_position_stays_there_and_one_with_neither_walks_near_the_default_centre()
    {
        var routePath = Path.Combine(Directory.CreateTempSubdirectory("fieldtrack-route-").FullName, "line.gpx");
        File.WriteAllText(routePath, """<gpx><trk><trkseg><trkpt lat="10" lon="20"/><trkpt lat="10.1" lon="20"/></trkseg></trk></gpx>""");
        var onRoute = new FleetDevice("A", "A", AssetType.Truck, routePath, null, 36, []);
        var fixedAt = new FleetDevice("B", "B", AssetType.Generator, null, new Position(1, 2), 36, []);
        var walking = new FleetDevice("C", "C", AssetType.Truck, null, null, 36, []);

        var positions = new[] { onRoute, fixedAt, walking }.Select(spec => PositionOf(SimulatedDevice.Start(spec, 1), TimeSpan.Zero)).ToArray();

        positions[0].Lat.ShouldBe(10, tolerance: 1e-6);
        positions[1].ShouldBe(new Position(1, 2));
        Geo.Distance(positions[2], RandomWalkMotion.DefaultCentre).ShouldBeLessThan(1500);
    }

    [Fact]
    public void A_missing_route_file_stops_the_Device_from_starting()
    {
        var spec = new FleetDevice("A", "A", AssetType.Truck, "no-such.gpx", null, 36, []);

        Should.Throw<SimulatorException>(() => SimulatedDevice.Start(spec, 1)).Message.ShouldContain("no-such.gpx");
    }

    [Fact]
    public void The_topic_is_the_contract_topic_for_the_serial_number()
    {
        Truck().Topic.ShouldBe("fieldtrack/v1/devices/TRK-001/telemetry");
    }

    private static Position PositionOf(SimulatedDevice device, TimeSpan elapsed)
    {
        var root = JsonDocument.Parse(device.NextPayload(Now, elapsed)).RootElement;
        return new Position(root.GetProperty("lat").GetDouble(), root.GetProperty("lon").GetDouble());
    }

    private static long Sequence(byte[] payload) => JsonDocument.Parse(payload).RootElement.GetProperty("seq").GetInt64();

    private static double Fuel(byte[] payload) => JsonDocument.Parse(payload).RootElement.GetProperty("fuelPct").GetDouble();
}
