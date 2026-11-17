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
    public void A_sensor_with_a_sigma_scatters_around_its_normal_value_and_one_without_stays_exact()
    {
        var spec = new FleetDevice(
            "TRK-001", "Haul truck 1", AssetType.Truck, null, new Position(45.5, -122.6), 30, [Sensor.EngineTempC, Sensor.BatteryV],
            new Dictionary<Sensor, double> { [Sensor.EngineTempC] = 2 });
        var device = new SimulatedDevice(spec, 1, new FixedMotion(new Position(45.5, -122.6)));

        var readings = Enumerable.Range(0, 3000).Select(second => JsonDocument.Parse(Take(device, TimeSpan.FromSeconds(second))).RootElement).ToList();

        var temperatures = readings.Select(reading => reading.GetProperty("engineTempC").GetDouble()).ToList();
        temperatures.Average().ShouldBe(85, tolerance: 0.2);
        Math.Sqrt(temperatures.Select(t => Math.Pow(t - temperatures.Average(), 2)).Average()).ShouldBe(2, tolerance: 0.15);
        readings.ShouldAllBe(reading => reading.GetProperty("batteryV").GetDouble() == 13.8);
    }

    [Fact]
    public void A_payload_is_a_Reading_by_the_telemetry_contract()
    {
        var device = Truck(boot: 3, [.. Enum.GetValues<Sensor>()]);

        var payload = Take(device, TimeSpan.FromMinutes(10));

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

        using var document = JsonDocument.Parse(Take(device, TimeSpan.Zero));

        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Order().ShouldBe(["batteryV", "boot", "fuelPct", "lat", "lon", "seq", "ts"]);
    }

    [Fact]
    public void The_Sequence_number_moves_on_only_when_a_Reading_was_published()
    {
        var device = Truck();

        var first = Take(device, TimeSpan.Zero);
        var next = Take(device, TimeSpan.FromSeconds(1));

        Sequence(first).ShouldBe(0);
        Sequence(next).ShouldBe(1);
        device.PublishedCount.ShouldBe(2);
        device.Ranges.ShouldBe([new PublishedRange(3, 0, 1)]);
    }

    [Fact]
    public void A_retry_later_in_the_run_sends_the_same_bytes_until_the_Reading_was_published()
    {
        // A random walk moves on with every call, so rebuilding the payload would change its position.
        var spec = new FleetDevice("TRK-001", "Haul truck 1", AssetType.Truck, null, null, 36, [Sensor.FuelPct]);
        var device = new SimulatedDevice(spec, 1, new RandomWalkMotion(RandomWalkMotion.DefaultCentre, 36, 1500, new Random(1)));

        device.Step(Now, TimeSpan.FromSeconds(1));
        var first = device.NextToSend;
        var retried = device.NextToSend;
        device.Published();
        var next = Take(device, TimeSpan.FromSeconds(32));

        first.ShouldNotBeNull();
        retried.ShouldBe(first);
        next.ShouldNotBe(first);
        Sequence(next).ShouldBe(1);
    }

    [Fact]
    public void Fuel_drains_over_time_and_starts_again_when_it_runs_low()
    {
        var device = Truck(sensors: [Sensor.FuelPct]);

        var fuel = Enumerable.Range(0, 400).Select(minute => Fuel(Take(device, TimeSpan.FromMinutes(minute)))).ToArray();

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
        var root = JsonDocument.Parse(Take(device, elapsed)).RootElement;
        return new Position(root.GetProperty("lat").GetDouble(), root.GetProperty("lon").GetDouble());
    }

    // One tick of a healthy Device with a working broker: it takes a Reading and the broker acknowledges it.
    private static byte[] Take(SimulatedDevice device, TimeSpan elapsed)
    {
        device.Step(Now, elapsed);
        var payload = device.NextToSend!;
        device.Published();
        return payload;
    }

    private static long Sequence(byte[] payload) => JsonDocument.Parse(payload).RootElement.GetProperty("seq").GetInt64();

    private static double Fuel(byte[] payload) => JsonDocument.Parse(payload).RootElement.GetProperty("fuelPct").GetDouble();
}
