using System.Text.Json;
using FieldTrack.Simulator;

namespace FieldTrack.IntegrationTests;

public sealed class SimulatorRunTests(FieldTrackFixture fixture) : IntegrationTest(fixture), IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-run-").FullName;

    // Two ways to move: a random walk and a fixed position (routes are covered by the unit tests).
    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "TRK-001", "assetName": "Truck 1", "assetType": "Truck", "speedKph": 40,
            "sensors": ["speedKph", "engineTempC", "fuelPct", "batteryV"] },
          { "serial": "GEN-001", "assetName": "Generator 1", "assetType": "Generator",
            "position": { "lat": 45.5231, "lon": -122.6765 }, "sensors": ["fuelPct", "vibrationMmS"] },
          { "serial": "SUB-001", "assetName": "Substation 1", "assetType": "Sensor",
            "position": { "lat": 45.5262, "lon": -122.6742 } }
        ] }
        """, AppContext.BaseDirectory);

    private RunOptions Options(TimeSpan duration, double rate = 30) =>
        new(MqttBroker.Tcp(Fixture.BrokerHost, Fixture.BrokerPort), rate, duration, Path.Combine(_directory, "state.json"), Path.Combine(_directory, "manifest.json"));

    private async Task SeedAsync()
    {
        var api = await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(api, Fleet, TextWriter.Null, CancellationToken);
    }

    private static async Task<JsonElement[]> ReadingsAsync(HttpClient admin, JsonElement asset) =>
        await ListAsync(admin, $"/assets/{asset.GetProperty("id").GetString()}/readings?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}");

    private static int Published(RunManifest manifest, string serial) =>
        manifest.Devices.Single(device => device.Serial == serial).Ranges.Sum(range => (int)(range.LastSequence - range.FirstSequence + 1));

    [Fact]
    public async Task Published_Readings_are_stored_and_the_Manifest_lists_exactly_what_the_API_returns()
    {
        await SeedAsync();
        await using var ingest = await StartIngestAsync();
        using var admin = await AdminClientAsync(ingest);

        var manifest = await FleetRunner.RunAsync(Fleet, Options(TimeSpan.FromSeconds(3)), TextWriter.Null, TimeProvider.System, CancellationToken);

        var assets = (await ListAsync(admin, "/assets")).ToDictionary(asset => asset.GetProperty("name").GetString()!);
        foreach (var (serial, assetName) in new[] { ("TRK-001", "Truck 1"), ("GEN-001", "Generator 1"), ("SUB-001", "Substation 1") })
        {
            var expected = Published(manifest, serial);
            expected.ShouldBeInRange(15, 40, $"{serial}: 3 s at 30/s over 3 Devices is about 30 each");
            var readings = await EventuallyAsync(() => ReadingsAsync(admin, assets[assetName]), stored => stored.Length >= expected);

            readings.Length.ShouldBe(expected, serial);
            readings.Select(reading => reading.GetProperty("sequenceNumber").GetInt64()).ShouldBe(Enumerable.Range(0, expected).Select(i => (long)i));
            readings.ShouldAllBe(reading => reading.GetProperty("boot").GetInt32() == 1 && reading.GetProperty("deviceSerialNumber").GetString() == serial);
        }

        var truck = await ReadingsAsync(admin, assets["Truck 1"]);
        truck.Select(reading => reading.GetProperty("latitude").GetDouble()).Distinct().Count().ShouldBeGreaterThan(5, "the random walk moves");
        truck[0].GetProperty("speedKph").GetSingle().ShouldBe(40f);
        var generator = await ReadingsAsync(admin, assets["Generator 1"]);
        generator.ShouldAllBe(reading => Math.Abs(reading.GetProperty("latitude").GetDouble() - 45.5231) < 1e-6 && Math.Abs(reading.GetProperty("longitude").GetDouble() + 122.6765) < 1e-6);
        generator[0].GetProperty("speedKph").ValueKind.ShouldBe(JsonValueKind.Null);
        generator[0].GetProperty("vibrationMmS").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    [Fact]
    public async Task Each_start_raises_the_Boot_and_restarts_the_Sequence_number_at_0_and_the_Manifest_file_says_so()
    {
        await SeedAsync();
        await using var ingest = await StartIngestAsync();
        using var admin = await AdminClientAsync(ingest);

        await FleetRunner.RunAsync(Fleet, Options(TimeSpan.FromSeconds(1)), TextWriter.Null, TimeProvider.System, CancellationToken);
        var second = await FleetRunner.RunAsync(Fleet, Options(TimeSpan.FromSeconds(1)), TextWriter.Null, TimeProvider.System, CancellationToken);

        var trucks = second.Devices.Single(device => device.Serial == "TRK-001").Ranges.ShouldHaveSingleItem();
        trucks.Boot.ShouldBe(2);
        trucks.FirstSequence.ShouldBe(0);
        var onDisk = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "manifest.json"), CancellationToken)).RootElement;
        onDisk.GetProperty("devices").EnumerateArray()
            .Single(device => device.GetProperty("serial").GetString() == "TRK-001")
            .GetProperty("ranges")[0].GetProperty("boot").GetInt32().ShouldBe(2);
        var assets = await ListAsync(admin, "/assets");
        var truck = assets.Single(asset => asset.GetProperty("name").GetString() == "Truck 1");
        var readings = await EventuallyAsync(
            () => ReadingsAsync(admin, truck),
            stored => stored.Any(reading => reading.GetProperty("boot").GetInt32() == 2));
        readings.Select(reading => (reading.GetProperty("boot").GetInt32(), reading.GetProperty("sequenceNumber").GetInt64()))
            .ShouldBeUnique();
        readings.Where(reading => reading.GetProperty("boot").GetInt32() == 2).First().GetProperty("sequenceNumber").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_a_run_stops_it_and_still_writes_the_Manifest()
    {
        await SeedAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var running = FleetRunner.RunAsync(Fleet, Options(duration: TimeSpan.FromMinutes(10)), TextWriter.Null, TimeProvider.System, stop.Token);

        await Task.Delay(TimeSpan.FromSeconds(1.5), CancellationToken);
        await stop.CancelAsync();
        var manifest = await running;

        File.Exists(Path.Combine(_directory, "manifest.json")).ShouldBeTrue();
        manifest.EndedAt.ShouldBeLessThan(manifest.StartedAt.AddSeconds(30));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
