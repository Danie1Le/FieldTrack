using FieldTrack.Api.Ingest;
using FieldTrack.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Guards ingest's throughput once the broker holds a backlog: the broker connection's TCP buffers hold every message in
/// flight, and a flood's backlog is stored within seconds (#116).
/// </summary>
public sealed class IngestThroughputTests(FieldTrackFixture fixture) : IntegrationTest(fixture), IDisposable
{
    // A Reading as an MQTT PUBLISH is about 200 bytes (topic, header and a payload with a few sensors).
    private const int ReadingMessageBytes = 256;

    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-throughput-").FullName;

    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "TRK-001", "assetName": "Truck 1", "assetType": "Truck", "position": { "lat": 45.5231, "lon": -122.6765 },
            "sensors": ["engineTempC", "fuelPct"] },
          { "serial": "GEN-001", "assetName": "Generator 1", "assetType": "Generator", "position": { "lat": 45.5262, "lon": -122.6742 },
            "sensors": ["batteryV"] },
          { "serial": "SUB-001", "assetName": "Substation 1", "assetType": "Sensor", "position": { "lat": 45.5300, "lon": -122.6700 },
            "sensors": ["batteryV"] }
        ] }
        """, AppContext.BaseDirectory);

    [Fact]
    public async Task Ingest_connects_with_TCP_buffers_that_hold_every_message_the_broker_may_have_in_flight()
    {
        await using var api = await StartIngestAsync();
        var worker = api.Services.GetServices<IHostedService>().OfType<IngestWorker>().Single();

        var clientOptions = worker.ClientOptions();
        var tcp = clientOptions.ChannelOptions.ShouldBeOfType<MqttClientTcpOptions>();

        tcp.BufferSize.ShouldBeGreaterThanOrEqualTo(clientOptions.ReceiveMaximum * ReadingMessageBytes);
    }

    [Fact]
    public async Task Ingest_stores_the_backlog_a_flood_leaves_in_the_broker_within_seconds()
    {
        var api = await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(api, Fleet, TextWriter.Null, CancellationToken);
        await using var ingest = await StartIngestAsync();
        var flood = new RunOptions(
            Fixture.BrokerHost, Fixture.BrokerPort, RatePerSecond: null, TimeSpan.FromSeconds(2),
            Path.Combine(_directory, "state.json"), Path.Combine(_directory, "manifest.json"));

        var manifest = await FleetRunner.RunAsync(Fleet, flood, TextWriter.Null, TimeProvider.System, CancellationToken);

        var published = manifest.Devices.Sum(device => device.Ranges.Sum(range => range.LastSequence - range.FirstSequence + 1));
        await using var database = await OpenDatabaseAsync();
        // Healthy ingest stores a flood's leftover backlog in about a second; with an 8 KB window even the smallest CI
        // flood (~20,000 Readings) would need over a minute.
        var stored = await EventuallyAsync(
            async () => (long)(await SqlAsync(database, "SELECT count(*) FROM readings"))!,
            count => count >= published,
            within: TimeSpan.FromSeconds(15));
        stored.ShouldBe(published);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
