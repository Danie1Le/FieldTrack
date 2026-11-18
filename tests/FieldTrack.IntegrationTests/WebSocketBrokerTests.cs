using System.Globalization;
using System.Text;
using System.Text.Json;
using FieldTrack.Api.Health;
using FieldTrack.Api.Ingest;
using FieldTrack.Infrastructure.Ingest;
using FieldTrack.Infrastructure.Messaging;
using FieldTrack.Simulator;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FieldTrack.IntegrationTests;

/// <summary>Ingest and the simulator against the cloud broker image, over MQTT over WebSockets with passwords (#154).</summary>
public sealed class WebSocketBrokerTests(FieldTrackFixture fixture, CloudBroker broker) : IntegrationTest(fixture), IClassFixture<CloudBroker>
{
    [Fact]
    public async Task Ingest_stores_what_the_simulator_publishes_over_WebSockets_across_a_restart_of_ingest()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-WS" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck WS", type = "Truck" });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        // Its own broker, so the session needs no dropping at the end, unlike IntegrationTest.NewIngestClientId.
        var clientId = $"fieldtrack-ingest-test-{Guid.NewGuid():N}";
        await TelemetryPublisher.CreateIngestSessionAsync(broker.Ingest, clientId);
        await using var simulator = new ReconnectingPublisher(broker.Simulator, "TRK-WS", TextWriter.Null);
        var topic = TelemetryPublisher.TopicOf("TRK-WS");

        // Published before ingest connects: the persistent session holds it (ADR 0004).
        await simulator.PublishAsync(topic, Reading(0), CancellationToken);
        await using (StartIngest(clientId))
        {
            await HistoryWhenAsync(admin, asset, count: 1);
        }

        // Published while ingest is away, then delivered when it reconnects to the same session.
        await simulator.PublishAsync(topic, Reading(1), CancellationToken);
        await using (StartIngest(clientId))
        {
            var history = await HistoryWhenAsync(admin, asset, count: 2);
            history.Select(reading => reading.GetProperty("sequenceNumber").GetInt64()).ShouldBe([0L, 1L]);
        }
    }

    [Fact]
    public async Task A_Reading_whose_batch_crashed_before_commit_is_redelivered_over_WebSockets_and_stored_once()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-WS-CRASH" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck WS crash", type = "Truck" });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        var clientId = $"fieldtrack-ingest-test-{Guid.NewGuid():N}";
        await TelemetryPublisher.CreateIngestSessionAsync(broker.Ingest, clientId);
        await using var simulator = new ReconnectingPublisher(broker.Simulator, "TRK-WS-CRASH", TextWriter.Null);
        await simulator.PublishAsync(TelemetryPublisher.TopicOf("TRK-WS-CRASH"), Reading(0), CancellationToken);

        // ADR 0004: the batch dies after staging, before its commit, so ingest never acknowledges the message.
        var crash = new IngestCrashTests.CrashAt(IngestCrashPoint.AfterStage);
        await using (StartIngest(clientId, crash))
        {
            await crash.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken);
            (await ListAsync(admin, $"/assets/{asset}/readings?{HistoryRange()}")).ShouldBeEmpty();
        }

        // The broker redelivers it over WebSockets to the next ingest host on the same session.
        await using (StartIngest(clientId))
        {
            (await HistoryWhenAsync(admin, asset, count: 1)).ShouldHaveSingleItem().GetProperty("sequenceNumber").GetInt64().ShouldBe(0L);
        }
    }

    [Fact]
    public async Task The_cloud_broker_refuses_a_client_with_a_wrong_password()
    {
        var wrong = broker.Ingest;
        wrong.Password = "wrong";

        var result = await new BrokerHealthCheck(Microsoft.Extensions.Options.Options.Create(wrong)).CheckHealthAsync(new HealthCheckContext(), CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldEndWith("refused the connection: NotAuthorized.");
    }

    private static byte[] Reading(long seq) => Encoding.UTF8.GetBytes(TelemetryPublisher.Payload(1, seq, DateTimeOffset.UtcNow));

    // A host running ingest against the cloud broker, configured the way the deployment configures it, and crashing
    // where crashes says when given.
    private WebApplicationFactory<Program> StartIngest(string clientId, IIngestCrashPoints? crashes = null)
    {
        var settings = broker.Ingest;
        var api = Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting(FieldTrackApiFactory.BrokerHostKey, settings.Host)
            .UseSetting(FieldTrackApiFactory.BrokerPortKey, settings.Port.ToString(CultureInfo.InvariantCulture))
            .UseSetting($"{BrokerOptions.SectionName}:{nameof(BrokerOptions.Transport)}", settings.Transport.ToString())
            .UseSetting($"{BrokerOptions.SectionName}:{nameof(BrokerOptions.Username)}", settings.Username)
            .UseSetting($"{BrokerOptions.SectionName}:{nameof(BrokerOptions.Password)}", settings.Password)
            .UseSetting($"{IngestOptions.SectionName}:{nameof(IngestOptions.ClientId)}", clientId)
            .ConfigureTestServices(services =>
            {
                services.AddHostedService<IngestWorker>();
                if (crashes is not null)
                {
                    services.AddSingleton(crashes);
                }
            }));
        api.StartServer();
        return api;
    }

    private static async Task<JsonElement[]> HistoryWhenAsync(HttpClient admin, string asset, int count)
    {
        var range = HistoryRange();
        var history = await EventuallyAsync(() => ListAsync(admin, $"/assets/{asset}/readings?{range}"), readings => readings.Length >= count);
        history.Length.ShouldBe(count);
        return history;
    }

    private static string HistoryRange() =>
        $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture))}"
        + $"&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture))}";
}
