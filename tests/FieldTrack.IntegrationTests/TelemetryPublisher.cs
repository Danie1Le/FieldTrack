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
    /// Creates the broker session an ingest worker with <paramref name="clientId"/> resumes, subscribed to
    /// <paramref name="topicFilter"/>, so messages published from now on wait for it however long it takes to connect.
    /// Resuming a session a crashed worker left behind acknowledges nothing, so the next worker gets its messages.
    /// </summary>
    public static async Task CreateIngestSessionAsync(FieldTrackFixture fixture, string clientId, string topicFilter = IngestWorker.TopicFilter)
    {
        using var client = await ConnectWithoutAcknowledgingAsync(fixture, clientId);
        await client.SubscribeAsync(topicFilter, MqttQualityOfServiceLevel.AtLeastOnce, TestContext.Current.CancellationToken);
        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Discards the broker session of <paramref name="clientId"/> and everything queued for it; nothing happens when there is none.</summary>
    public static async Task DropIngestSessionAsync(FieldTrackFixture fixture, string clientId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(Options(fixture, clientId).WithCleanStart().WithSessionExpiryInterval(0).Build(), timeout.Token);
        await client.DisconnectAsync(cancellationToken: timeout.Token);
    }

    /// <summary>
    /// Connects to the ingest worker's broker session and leaves again without acknowledging anything. The broker
    /// drops the worker's connection, so its PUBACKs never arrive, as if it had crashed.
    /// </summary>
    public static async Task TakeOverIngestSessionAsync(FieldTrackFixture fixture, string clientId)
    {
        using var client = await ConnectWithoutAcknowledgingAsync(fixture, clientId);
        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<IMqttClient> ConnectWithoutAcknowledgingAsync(FieldTrackFixture fixture, string clientId)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args =>
        {
            args.AutoAcknowledge = false;
            return Task.CompletedTask;
        };
        await client.ConnectAsync(IngestSessionOptions(fixture, clientId), TestContext.Current.CancellationToken);
        return client;
    }

    /// <summary>A contract payload with the required fields and any extra JSON members in <paramref name="more"/>; its Device time is <see cref="MillisUp"/>.</summary>
    public static string Payload(int boot, long seq, DateTimeOffset deviceTime, double lat = 51.5, double lon = -0.12, string more = "") =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"boot\":{boot},\"seq\":{seq},\"ts\":{MillisUp(deviceTime).ToUnixTimeMilliseconds()},\"lat\":{lat},\"lon\":{lon}{more}}}");

    /// <summary>
    /// What a Device time keeps at the contract's millisecond precision: rounded up, never down. An Attachment covers only
    /// Device times at or after its own (microsecond) instant, so a Reading taken right after an attach, sometimes in the same
    /// millisecond on a fast runner, must not truncate to before it and miss the Asset (#142).
    /// </summary>
    public static DateTimeOffset MillisUp(DateTimeOffset time) =>
        DateTimeOffset.FromUnixTimeMilliseconds((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);

    public Task PublishAsync(string serialNumber, string payload) => PublishToAsync(TopicOf(serialNumber), payload);

    public static string TopicOf(string serialNumber) => $"fieldtrack/{IngestWorker.ContractVersion}/devices/{serialNumber}/telemetry";

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

    private static MqttClientOptions IngestSessionOptions(FieldTrackFixture fixture, string clientId) =>
        Options(fixture, clientId).WithCleanStart(false).WithSessionExpiryInterval(IngestWorker.SessionExpirySeconds).Build();

    private static MqttClientOptionsBuilder Options(FieldTrackFixture fixture, string clientId) =>
        new MqttClientOptionsBuilder().WithTcpServer(fixture.BrokerHost, fixture.BrokerPort).WithClientId(clientId);
}
