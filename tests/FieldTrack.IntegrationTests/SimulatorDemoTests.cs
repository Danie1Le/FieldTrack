using FieldTrack.Simulator;

namespace FieldTrack.IntegrationTests;

public sealed class SimulatorDemoTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    // The demo fleet's Assets the demo's rules name (fleets/demo.json).
    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "TRK-001", "assetName": "Haul truck 1", "assetType": "Truck" },
          { "serial": "TRK-002", "assetName": "Haul truck 2", "assetType": "Truck" },
          { "serial": "EXC-001", "assetName": "Excavator 1", "assetType": "Excavator" },
          { "serial": "GEN-001", "assetName": "Generator 1", "assetType": "Generator", "position": { "lat": 45.5231, "lon": -122.6765 } }
        ] }
        """, AppContext.BaseDirectory);

    private async Task<FieldTrackApi> SeededAsync()
    {
        var api = await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(api, Fleet, TextWriter.Null, CancellationToken);
        return api;
    }

    private async Task<(string Name, string Kind, string Asset)[]> RulesAsync()
    {
        using var admin = await AdminClientAsync();
        var assets = (await ListAsync(admin, "/assets")).ToDictionary(asset => asset.GetProperty("id").GetString()!, asset => asset.GetProperty("name").GetString()!);
        return [.. (await ListAsync(admin, "/alert-rules"))
            .Select(rule => (rule.GetProperty("name").GetString()!, rule.GetProperty("kind").GetString()!, assets[rule.GetProperty("assetId").GetString()!]))
            .OrderBy(rule => rule.Item1)];
    }

    [Fact]
    public async Task The_demo_adds_its_Geofence_and_a_rule_for_each_event_it_shows()
    {
        var api = await SeededAsync();

        await DemoSetup.ApplyAsync(api, TextWriter.Null, CancellationToken);

        using var admin = await AdminClientAsync();
        (await ListAsync(admin, "/geofences")).Select(geofence => geofence.GetProperty("name").GetString()).ShouldBe([DemoSetup.YardName]);
        (await RulesAsync()).ShouldBe([
            ("Demo: Generator 1 offline", "Offline", "Generator 1"),
            ("Demo: Generator 1 overheat", "Threshold", "Generator 1"),
            ("Demo: Haul truck 1 overheat", "Threshold", "Haul truck 1"),
            ("Demo: Haul truck 2 left North yard", "GeofenceExit", "Haul truck 2"),
        ]);
    }

    [Fact]
    public async Task Setting_up_the_demo_twice_adds_nothing_the_second_time()
    {
        var api = await SeededAsync();
        await DemoSetup.ApplyAsync(api, TextWriter.Null, CancellationToken);

        await DemoSetup.ApplyAsync(api, TextWriter.Null, CancellationToken);

        using var admin = await AdminClientAsync();
        (await ListAsync(admin, "/geofences")).Length.ShouldBe(1);
        (await RulesAsync()).Length.ShouldBe(4);
    }

    [Fact]
    public async Task The_demo_needs_the_demo_fleet_seeded_first()
    {
        var api = await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);

        var exception = await Should.ThrowAsync<SimulatorException>(() => DemoSetup.ApplyAsync(api, TextWriter.Null, CancellationToken));

        exception.Message.ShouldContain("Haul truck 1");
    }
}
