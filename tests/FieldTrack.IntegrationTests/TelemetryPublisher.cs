using System.Globalization;
using System.Text;
using FieldTrack.Api.Ingest;
using MQTTnet;
using MQTTnet.Protocol;

namespace FieldTrack.IntegrationTests;

/// <summary>Publishes telemetry to the test broker the way a Device does: MQTT 5, QoS 1, its own topic.</summary>
internal sealed class TelemetryPublisher : IAsyncDisposable
{
    private readonly IMqttClient _client;

    private TelemetryPublisher(IMqttClient client) => _client = client;

    public static async Task<TelemetryPublisher> ConnectAsync(FieldTrackFixture fixture)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(Options(fixture, $"test-device-{Guid.NewGuid():N}").WithCleanStart().Build(), TestContext.Current.CancellationToken);
        return new TelemetryPublisher(client);
    }

    /// <summary>
    /// Creates the broker session an ingest worker with <paramref name="clientId"/> resumes, so messages
    /// published from now on wait for it however long it takes to connect.
    /// </summary>
    public static async Task CreateIngestSessionAsync(FieldTrackFixture fixture, string clientId)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        var options = Options(fixture, clientId).WithCleanStart(false).WithSessionExpiryInterval(IngestWorker.SessionExpirySeconds).Build();
        await client.ConnectAsync(options, TestContext.Current.CancellationToken);
        await client.SubscribeAsync(IngestWorker.TopicFilter, MqttQualityOfServiceLevel.AtLeastOnce, TestContext.Current.CancellationToken);
        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>A contract payload with the required fields and any extra JSON members in <paramref name="more"/>.</summary>
    public static string Payload(int boot, long seq, DateTimeOffset deviceTime, double lat = 51.5, double lon = -0.12, string more = "") =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"boot\":{boot},\"seq\":{seq},\"ts\":{deviceTime.ToUnixTimeMilliseconds()},\"lat\":{lat},\"lon\":{lon}{more}}}");

    public Task PublishAsync(string serialNumber, string payload) => PublishToAsync(TopicOf(serialNumber), payload);

    public static string TopicOf(string serialNumber) => $"fieldtrack/v1/devices/{serialNumber}/telemetry";

    public async Task PublishToAsync(string topic, string payload)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        var result = await _client.PublishAsync(message, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisconnectAsync();
        _client.Dispose();
    }

    private static MqttClientOptionsBuilder Options(FieldTrackFixture fixture, string clientId) =>
        new MqttClientOptionsBuilder().WithTcpServer(fixture.BrokerHost, fixture.BrokerPort).WithClientId(clientId);
}
