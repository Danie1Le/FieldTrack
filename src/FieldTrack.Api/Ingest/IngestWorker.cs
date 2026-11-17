using System.Buffers;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FieldTrack.Api.Messaging;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Ingest;
using FieldTrack.Infrastructure.Messaging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using Npgsql;

namespace FieldTrack.Api.Ingest;

/// <summary>
/// Subscribes to Device telemetry and stores each Reading exactly once (ADR 0004): messages are batched,
/// each batch is written in one transaction, and PUBACKs go out only after it commits, in receive order.
/// A crash before that leaves the messages unacknowledged in the broker session, which redelivers them
/// on the next connect; the Reading key table drops any copy already stored (ADR 0003). A message that cannot
/// become a Reading is acknowledged the same way, stored as a Rejected message in its batch's transaction. The
/// worker is a single instance (spec #21).
/// </summary>
public sealed partial class IngestWorker(
    IOptions<BrokerOptions> broker,
    IOptions<IngestOptions> options,
    NpgsqlDataSource dataSource,
    TimeProvider time,
    IMeterFactory meters,
    ILogger<IngestWorker> logger) : BackgroundService
{
    /// <summary>
    /// Every Device's telemetry topic, <c>fieldtrack/{version}/devices/{serialNumber}/telemetry</c>, in every
    /// version, so a message on a version other than <see cref="ContractVersion"/> is rejected instead of left unread.
    /// </summary>
    public const string TopicFilter = "fieldtrack/+/devices/+/telemetry";

    /// <summary>The one version of the telemetry contract ingest reads (spec #21).</summary>
    public const string ContractVersion = "v1";

    /// <summary>The meter ingest reports on.</summary>
    public const string MeterName = "FieldTrack.Ingest";

    /// <summary>The counter of Rejected messages stored, tagged with their <c>reason</c>.</summary>
    public const string RejectedMessagesInstrument = "fieldtrack.ingest.rejected_messages";

    /// <summary>How long the broker keeps the session, and its queued messages, while the worker is away.</summary>
    public const uint SessionExpirySeconds = 3_600;

    /// <summary>A batch closes at this many messages...</summary>
    public const int BatchSize = 1_000;

    /// <summary>...or this long after its first message (ADR 0004: p95 publish-to-commit under 200 ms).</summary>
    public static readonly TimeSpan FlushAfter = TimeSpan.FromMilliseconds(25);

    private static readonly TimeSpan _retryAfter = TimeSpan.FromSeconds(2);

    private readonly Counter<long> _rejectedMessages = meters.Create(MeterName).CreateCounter<long>(
        RejectedMessagesInstrument, "{message}", "Telemetry messages stored as Rejected messages, by reason.");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // MQTT has no negative acknowledgement: the broker resends unacknowledged messages only on
                // reconnect, so every failure ends the session and starts a new one.
                LogSessionFailed(exception, _retryAfter);
                try
                {
                    await Task.Delay(_retryAfter, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        // The MQTT client calls the handler for one message at a time, so channel order is receive order.
        // ReceiveMaximum bounds only QoS 1 messages; a full channel makes the handler wait, which stalls the
        // client's receive loop and so pushes back on a QoS 0 flood instead of growing memory. The capacity
        // holds every QoS 1 message the broker may have in flight, so those never wait.
        var received = Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(2 * BatchSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += async args =>
        {
            args.AutoAcknowledge = false;
            try
            {
                await received.Writer.WriteAsync(new ReceivedMessage(args, time.GetUtcNow()));
            }
            catch (ChannelClosedException)
            {
                // The session is ending; the unacknowledged message is redelivered on the next connect.
            }
        };
        client.DisconnectedAsync += args =>
        {
            received.Writer.TryComplete(new MqttCommunicationException($"Disconnected from the broker: {args.Reason}."));
            return Task.CompletedTask;
        };

        await using var writer = await ReadingWriter.OpenAsync(dataSource, stoppingToken);
        var connected = await client.ConnectAsync(ClientOptions(), stoppingToken);
        // MQTTnet returns a refused CONNACK (bad credentials, say) instead of throwing.
        if (connected.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new MqttCommunicationException($"The broker refused the connection: {connected.ResultCode}.");
        }

        // Subscribing again is harmless on a resumed session and restores the subscription if it expired.
        await client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder().WithTopicFilter(TopicFilter, MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            stoppingToken);
        LogConnected(options.Value.ClientId, connected.IsSessionPresent);

        try
        {
            while (true)
            {
                var batch = await NextBatchAsync(received.Reader, stoppingToken);
                var (readings, rejected) = Parse(batch);
                foreach (var reason in await writer.WriteAsync(readings, rejected, stoppingToken))
                {
                    _rejectedMessages.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
                }

                // Committed: from here a crash only causes redelivery of stored Readings, which the key
                // table drops, so finish acknowledging even while stopping.
                foreach (var message in batch)
                {
                    await message.Args.AcknowledgeAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            // Release a handler waiting on a full channel, so the client can disconnect.
            received.Writer.TryComplete();
            if (client.IsConnected)
            {
                await client.DisconnectAsync(cancellationToken: CancellationToken.None);
            }
        }
    }

    private MqttClientOptions ClientOptions() =>
        new MqttClientOptionsBuilder()
            .WithBroker(broker.Value)
            .WithClientId(options.Value.ClientId)
            .WithCleanStart(false)
            .WithSessionExpiryInterval(SessionExpirySeconds)
            // The broker sends at most this many unacknowledged messages, so a full batch never waits on its own acks.
            .WithReceiveMaximum(2 * BatchSize)
            .Build();

    private static async Task<List<ReceivedMessage>> NextBatchAsync(ChannelReader<ReceivedMessage> reader, CancellationToken stoppingToken)
    {
        var batch = new List<ReceivedMessage>(BatchSize) { await reader.ReadAsync(stoppingToken) };
        using var flush = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        flush.CancelAfter(FlushAfter);
        while (batch.Count < BatchSize)
        {
            if (reader.TryRead(out var message))
            {
                batch.Add(message);
                continue;
            }

            try
            {
                if (!await reader.WaitToReadAsync(flush.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        return batch;
    }

    /// <summary>
    /// Reads each message as a Reading, or as a Rejected message when its topic version or payload breaks the
    /// contract. Whether its Device is known and not Archived is decided when the batch is stored.
    /// </summary>
    private static (List<ReceivedReading> Readings, List<RejectedMessage> Rejected) Parse(List<ReceivedMessage> batch)
    {
        var readings = new List<ReceivedReading>(batch.Count);
        var rejected = new List<RejectedMessage>();
        foreach (var (args, receivedTime) in batch)
        {
            var topic = args.ApplicationMessage.Topic;
            var payload = args.ApplicationMessage.Payload.ToArray();
            // The subscription guarantees fieldtrack/{version}/devices/{serialNumber}/telemetry.
            var levels = topic.Split('/');
            if (levels[1] != ContractVersion)
            {
                rejected.Add(new RejectedMessage { Topic = topic, Payload = payload, Reason = RejectReason.UnknownTopicVersion, ReceivedTime = receivedTime });
            }
            else if (TelemetryPayload.TryParse(payload, receivedTime, out var reading, out var reason))
            {
                readings.Add(new ReceivedReading(topic, levels[3], reading, receivedTime, payload));
            }
            else
            {
                rejected.Add(new RejectedMessage { Topic = topic, Payload = payload, Reason = reason, ReceivedTime = receivedTime });
            }
        }

        return (readings, rejected);
    }

    [LoggerMessage(LogLevel.Information, "Ingest connected as {ClientId}, session resumed: {SessionPresent}.")]
    private partial void LogConnected(string clientId, bool sessionPresent);

    [LoggerMessage(LogLevel.Error, "Ingest session failed; reconnecting in {RetryAfter}.")]
    private partial void LogSessionFailed(Exception exception, TimeSpan retryAfter);
    private sealed record ReceivedMessage(MqttApplicationMessageReceivedEventArgs Args, DateTimeOffset ReceivedTime);
}
