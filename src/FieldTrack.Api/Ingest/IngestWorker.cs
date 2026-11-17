using System.Buffers;
using System.Security.Cryptography;
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
/// on the next connect; the Reading key table drops any copy already stored (ADR 0003). The worker is a
/// single instance (spec #21).
/// </summary>
public sealed partial class IngestWorker(
    IOptions<BrokerOptions> broker,
    IOptions<IngestOptions> options,
    NpgsqlDataSource dataSource,
    TimeProvider time,
    ILogger<IngestWorker> logger) : BackgroundService
{
    /// <summary>Every Device's telemetry topic, version 1 of the contract (spec #21).</summary>
    public const string TopicFilter = "fieldtrack/v1/devices/+/telemetry";

    /// <summary>How long the broker keeps the session, and its queued messages, while the worker is away.</summary>
    public const uint SessionExpirySeconds = 3_600;

    /// <summary>A batch closes at this many messages...</summary>
    public const int BatchSize = 1_000;

    /// <summary>...or this long after its first message (ADR 0004: p95 publish-to-commit under 200 ms).</summary>
    public static readonly TimeSpan FlushAfter = TimeSpan.FromMilliseconds(25);

    private static readonly TimeSpan _retryAfter = TimeSpan.FromSeconds(2);

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
        var received = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args =>
        {
            args.AutoAcknowledge = false;
            received.Writer.TryWrite(new Message(args, time.GetUtcNow()));
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += args =>
        {
            received.Writer.TryComplete(new MqttCommunicationException($"Disconnected from the broker: {args.Reason}."));
            return Task.CompletedTask;
        };

        await using var writer = await ReadingWriter.OpenAsync(dataSource, stoppingToken);
        var connected = await client.ConnectAsync(ClientOptions(), stoppingToken);
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
                await writer.WriteAsync(Accept(batch), stoppingToken);
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

    private static async Task<List<Message>> NextBatchAsync(ChannelReader<Message> reader, CancellationToken stoppingToken)
    {
        var batch = new List<Message>(BatchSize) { await reader.ReadAsync(stoppingToken) };
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

    private List<ReceivedReading> Accept(List<Message> batch)
    {
        var readings = new List<ReceivedReading>(batch.Count);
        foreach (var (args, receivedTime) in batch)
        {
            var message = args.ApplicationMessage;
            // The subscription matches fieldtrack/v1/devices/{serialNumber}/telemetry only.
            var serialNumber = message.Topic.Split('/')[3];
            var payload = message.Payload.ToArray();
            if (TelemetryPayload.TryParse(payload, receivedTime, out var reading, out var reason))
            {
                readings.Add(new ReceivedReading(serialNumber, reading, receivedTime, SHA256.HashData(payload)));
            }
            else
            {
                // shortcut: refused messages are only logged and acknowledged; #27 stores them as Rejected messages.
                LogRefused(serialNumber, reason);
            }
        }

        return readings;
    }

    [LoggerMessage(LogLevel.Information, "Ingest connected as {ClientId}, session resumed: {SessionPresent}.")]
    private partial void LogConnected(string clientId, bool sessionPresent);

    [LoggerMessage(LogLevel.Error, "Ingest session failed; reconnecting in {RetryAfter}.")]
    private partial void LogSessionFailed(Exception exception, TimeSpan retryAfter);

    [LoggerMessage(LogLevel.Warning, "Refused telemetry from {SerialNumber}: {Reason}.")]
    private partial void LogRefused(string serialNumber, RejectReason reason);

    private sealed record Message(MqttApplicationMessageReceivedEventArgs Args, DateTimeOffset ReceivedTime);
}
