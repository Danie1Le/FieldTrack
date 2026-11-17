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
                "sensors": ["speedKph", "fuelPct"] },
              { "serial": "GEN-001", "assetType": "generator", "position": { "lat": 45.5, "lon": -122.6 } }
            ] }
            """, Base);

        var truck = fleet.Devices[0];
        truck.Serial.ShouldBe("TRK-001");
        truck.AssetName.ShouldBe("Haul truck 1");
        truck.AssetType.ShouldBe(AssetType.Truck);
        truck.Route.ShouldBe(Path.Combine(Base, "routes", "loop.gpx"));
        truck.Sensors.ShouldBe([Sensor.SpeedKph, Sensor.FuelPct]);
        var generator = fleet.Devices[1];
        generator.AssetName.ShouldBe("GEN-001");
        generator.AssetType.ShouldBe(AssetType.Generator);
        generator.Position.ShouldBe(new Position(45.5, -122.6));
        generator.Route.ShouldBeNull();
        generator.Sensors.ShouldBeEmpty();
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
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck", "position": { "lat": 45.5 } } ] }""", "position")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetName": " ", "assetType": "Truck" } ] }""", "assetName")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetName": "", "assetType": "Truck" } ] }""", "assetName")]
    [InlineData("""{ "devices": [ { "serial": "A", "assetType": "Truck", "assetName": "Haul" }, { "serial": "B", "assetType": "Truck", "assetName": "Haul" } ] }""", "Devices A and B both use Asset 'Haul'")]
    [InlineData("""{ "devices": [ null ] }""", "no serial")]
    [InlineData("not json", "not valid JSON")]
    public void A_fleet_file_that_breaks_a_rule_is_refused_with_the_reason(string json, string reason)
    {
        var exception = Should.Throw<SimulatorException>(() => FleetFile.Parse(json, Base));

        exception.Message.ShouldContain(reason);
    }

    [Fact]
    public void An_Asset_name_longer_than_the_API_allows_is_refused()
    {
        var json = $$"""{ "devices": [ { "serial": "A", "assetName": "{{new string('x', 201)}}", "assetType": "Truck" } ] }""";

        Should.Throw<SimulatorException>(() => FleetFile.Parse(json, Base)).Message.ShouldContain("assetName");
    }
}
