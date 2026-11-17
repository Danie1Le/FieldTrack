using FieldTrack.Domain.Assets;
using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class FleetFileTests
{
    private static readonly string Base = Path.GetFullPath("fleets");

    [Fact]
    public void A_mobile_Device_has_a_route_relative_to_the_fleet_file_and_a_stationary_one_a_position()
    {
        var fleet = FleetFile.Parse("""
            { "devices": [
              { "serial": "TRK-001", "assetName": "Haul truck 1", "assetType": "Truck", "route": "routes/loop.gpx",
                "speedKph": 25, "sensors": ["speedKph", "fuelPct"] },
              { "serial": "GEN-001", "assetType": "generator", "position": { "lat": 45.5, "lon": -122.6 } }
            ] }
            """, Base);

        var truck = fleet.Devices[0];
        truck.Serial.ShouldBe("TRK-001");
        truck.AssetName.ShouldBe("Haul truck 1");
        truck.AssetType.ShouldBe(AssetType.Truck);
        truck.Route.ShouldBe(Path.Combine(Base, "routes", "loop.gpx"));
        truck.SpeedKph.ShouldBe(25);
        truck.Sensors.ShouldBe([Sensor.SpeedKph, Sensor.FuelPct]);
        var generator = fleet.Devices[1];
        generator.AssetName.ShouldBe("GEN-001");
        generator.AssetType.ShouldBe(AssetType.Generator);
        generator.Position.ShouldBe(new Position(45.5, -122.6));
        generator.SpeedKph.ShouldBe(30);
        generator.Route.ShouldBeNull();
        generator.Sensors.ShouldBeEmpty();
    }

    [Fact]
    public void The_committed_demo_fleet_loads_and_its_routes_exist()
    {
        var fleet = FleetFile.Load(Path.Combine(AppContext.BaseDirectory, "fleets", "demo.json"));

        fleet.Devices.ShouldNotBeEmpty();
        foreach (var route in fleet.Devices.Select(device => device.Route).OfType<string>())
        {
            GpxRoute.Load(route).LengthMeters.ShouldBeGreaterThan(500, route);
        }
    }

    [Fact]
    public void A_Device_with_neither_route_nor_position_is_allowed_and_random_walks()
    {
        var fleet = FleetFile.Parse("""{ "devices": [ { "serial": "TRK-001", "assetType": "Truck" } ] }""", Base);

        fleet.Devices.Single().ShouldSatisfyAllConditions(
            device => device.Route.ShouldBeNull(),
            device => device.Position.ShouldBeNull());
    }

    [Theory]
    [InlineData("""{ "devices": [] }""", "no devices")]
    [InlineData("""{ }""", "no devices")]
    [InlineData("""{ "devices": [ { "assetType": "Truck" } ] }""", "no serial")]
    [InlineData("""{ "devices": [ { "serial": "a/b", "assetType": "Truck" } ] }""", "Serial 'a/b'")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Tractor" } ] }""", "assetType")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "7" } ] }""", "assetType")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck", "sensors": ["rpm"] } ] }""", "unknown sensor 'rpm'")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck", "route": "r.gpx", "position": { "lat": 1, "lon": 1 } } ] }""", "not both")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck", "position": { "lat": 91, "lon": 1 } } ] }""", "position")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck" }, { "serial": "A", "assetType": "Sensor" } ] }""", "appears twice")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck", "speedKph": 0 } ] }""", "speedKph")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck", "speedKph": 500 } ] }""", "speedKph")]
    [InlineData("""{ "devices": [ null ] }""", "no serial")]
    [InlineData("not json", "not valid JSON")]
    public void A_fleet_file_that_breaks_a_rule_is_refused_with_the_reason(string json, string reason)
    {
        var exception = Should.Throw<SimulatorException>(() => FleetFile.Parse(json, Base));

        exception.Message.ShouldContain(reason);
    }
}
