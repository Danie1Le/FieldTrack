using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FieldTrack.Api.Live;
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
/// become a Reading is acknowledged the same way, stored as a Rejected message in its batch's transaction, and a
/// redelivered copy of one already stored is dropped too. After the PUBACKs, a separate push loop sends each
/// committed batch's changes to the dashboards through <see cref="LivePush"/>, best effort (ADR 0005). The worker is
/// a single instance (spec #21).
/// </summary>
public sealed partial class IngestWorker(
    IOptions<BrokerOptions> broker,
    IOptions<IngestOptions> options,
    NpgsqlDataSource dataSource,
    TimeProvider time,
    IMeterFactory meters,
    LivePush live,
    ILogger<IngestWorker> logger,
    IIngestCrashPoints? crashes = null) : BackgroundService
{
    /// <summary>
    /// Every Device's telemetry topic, <c>fieldtrack/{version}/devices/{serialNumber}/telemetry</c>, in every
    /// version, so a message on a version other than <see cref="ContractVersion"/> is rejected instead of left unread.
    /// </summary>
    public const string TopicFilter = "fieldtrack/+/devices/+/telemetry";

    /// <summary>The tag on <see cref="RejectedMessagesInstrument"/> holding the <see cref="RejectReason"/> name.</summary>
    public const string ReasonTag = "reason";

    /// <summary>The one version of the telemetry contract ingest reads (spec #21).</summary>
    public const string ContractVersion = "v1";

    /// <summary>The meter ingest reports on.</summary>
    public const string MeterName = "FieldTrack.Ingest";

    /// <summary>The counter of Rejected messages stored, tagged with their <c>reason</c>.</summary>
    public const string RejectedMessagesInstrument = "fieldtrack.ingest.rejected_messages";

    /// <summary>The counter of Readings dropped as identical duplicates: the same key and content hash as the stored copy (ADR 0003).</summary>
    public const string IdenticalDuplicatesInstrument = "fieldtrack.ingest.identical_duplicates";

    /// <summary>
    /// The counter of Readings dropped as conflicting duplicates: the same key as the stored copy but a different content
    /// hash, which a correct Device never sends, so it points at a Device bug (ADR 0001).
    /// </summary>
    public const string ConflictingDuplicatesInstrument = "fieldtrack.ingest.conflicting_duplicates";

    /// <summary>The counter of telemetry messages ingest has processed, stored or rejected: the ingest rate.</summary>
    public const string MessagesInstrument = "fieldtrack.ingest.messages";

    /// <summary>The histogram of how many messages each batch held.</summary>
    public const string BatchSizeInstrument = "fieldtrack.ingest.batch_size";

    /// <summary>The histogram of how long a batch took to store, evaluate the Alert Rules and commit.</summary>
    public const string BatchCommitDurationInstrument = "fieldtrack.ingest.batch_commit_duration";

    /// <summary>The histogram of each message's time from the broker handing it over to its batch committing (ADR 0004: p95 under 200 ms).</summary>
    public const string ReceiveToCommitDurationInstrument = "fieldtrack.ingest.receive_to_commit_duration";

    /// <summary>The histogram of how long evaluating the Alert Rules took within each batch's transaction.</summary>
    public const string RuleEvaluationDurationInstrument = "fieldtrack.ingest.rule_evaluation_duration";

    /// <summary>The counter of Alerts raised by ingest.</summary>
    public const string AlertsRaisedInstrument = "fieldtrack.ingest.alerts_raised";

    /// <summary>The source of the trace spans ingest starts.</summary>
    public const string ActivitySourceName = "FieldTrack.Ingest";

    /// <summary>How long the broker keeps the session, and its queued messages, while the worker is away.</summary>
    public const uint SessionExpirySeconds = 3_600;

    /// <summary>A batch closes at this many messages...</summary>
    public const int BatchSize = 1_000;

    /// <summary>...or this long after its first message (ADR 0004: p95 publish-to-commit under 200 ms).</summary>
    public static readonly TimeSpan FlushAfter = TimeSpan.FromMilliseconds(25);

    private static readonly TimeSpan _retryAfter = TimeSpan.FromSeconds(2);

    // Filters earlier versions subscribed to. A resumed session keeps them beside TopicFilter, and a message
    // matching both would be delivered, and so rejected, twice.
    private static readonly string[] _retiredTopicFilters = ["fieldtrack/v1/devices/+/telemetry"];

    // Seconds from 1 ms to 10 s, around ADR 0004's 200 ms target; the default boundaries would put every batch in the first bucket.
    private static readonly InstrumentAdvice<double> _durationBuckets = new()
    {
        HistogramBucketBoundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.2, 0.5, 1, 2.5, 5, 10],
    };

    private static readonly ActivitySource _activities = new(ActivitySourceName);

    // IMeterFactory hands out one Meter per name, so each instrument below shares it.
    private readonly Counter<long> _messages = meters.Create(MeterName).CreateCounter<long>(
        MessagesInstrument, "{message}", "Telemetry messages processed, stored or rejected.");

    private readonly Histogram<long> _batchSize = meters.Create(MeterName).CreateHistogram<long>(
        BatchSizeInstrument, "{message}", "Messages per ingest batch.",
        advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000] });

    private readonly Histogram<double> _batchCommitDuration = meters.Create(MeterName).CreateHistogram(
        BatchCommitDurationInstrument, "s", "Time to store a batch, evaluate the Alert Rules and commit.", advice: _durationBuckets);

    private readonly Histogram<double> _receiveToCommitDuration = meters.Create(MeterName).CreateHistogram(
        ReceiveToCommitDurationInstrument, "s", "Time from a message's receipt to its batch's commit.", advice: _durationBuckets);

    private readonly Histogram<double> _ruleEvaluationDuration = meters.Create(MeterName).CreateHistogram(
        RuleEvaluationDurationInstrument, "s", "Time to evaluate the Alert Rules within a batch's transaction.", advice: _durationBuckets);

    private readonly Counter<long> _alertsRaised = meters.Create(MeterName).CreateCounter<long>(
        AlertsRaisedInstrument, "{alert}", "Alerts raised by ingest.");

    private readonly Counter<long> _rejectedMessages = meters.Create(MeterName).CreateCounter<long>(
        RejectedMessagesInstrument, "{message}", "Telemetry messages stored as Rejected messages, by reason.");

    private readonly Counter<long> _identicalDuplicates = meters.Create(MeterName).CreateCounter<long>(
        IdenticalDuplicatesInstrument, "{reading}", "Copies of stored Readings dropped with the same content hash.");

    private readonly Counter<long> _conflictingDuplicates = meters.Create(MeterName).CreateCounter<long>(
        ConflictingDuplicatesInstrument, "{reading}", "Copies of stored Readings dropped with a different content hash.");

    /// <summary>How many committed batches may wait for the push loop before the oldest is dropped.</summary>
    public const int PushBacklog = 64;

    // #35: ingest never waits on a push. Batches queue here for the push loop; when it falls behind (a slow dashboard,
    // a slow database), the oldest batch's changes are dropped, which ADR 0005 allows: dashboards catch up on reload.
    private readonly Channel<WrittenBatch> _committed = Channel.CreateBounded<WrittenBatch>(
        new BoundedChannelOptions(PushBacklog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
        _ => LogPushDropped(logger));

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(IngestAsync(stoppingToken), PushAsync(stoppingToken));

    private async Task IngestAsync(CancellationToken stoppingToken)
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

        await using var writer = await ReadingWriter.OpenAsync(dataSource, time, stoppingToken, crashes);
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
        // Harmless when the session never had them: the broker answers NoSubscriptionExisted.
        foreach (var retired in _retiredTopicFilters)
        {
            await client.UnsubscribeAsync(retired, stoppingToken);
        }

        LogConnected(options.Value.ClientId, connected.IsSessionPresent);

        try
        {
            while (true)
            {
                var batch = await NextBatchAsync(received.Reader, stoppingToken);
                // The span covers the batch's own work, not the wait for its first message; Npgsql's spans nest in it.
                using var activity = _activities.StartActivity("ingest batch");
                var started = Stopwatch.GetTimestamp();
                var (readings, rejected) = Parse(batch);
                var written = await writer.WriteAsync(readings, rejected, stoppingToken);
                var committed = time.GetUtcNow();
                activity?.SetTag("fieldtrack.ingest.batch_size", batch.Count);
                _messages.Add(batch.Count);
                _batchSize.Record(batch.Count);
                _batchCommitDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                _ruleEvaluationDuration.Record(written.RuleEvaluation.TotalSeconds);
                foreach (var message in batch)
                {
                    _receiveToCommitDuration.Record(Math.Max(0, (committed - message.ReceivedTime).TotalSeconds));
                }

                _alertsRaised.Add(written.RaisedAlertIds.Count);
                foreach (var reason in written.Rejected)
                {
                    _rejectedMessages.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason.ToString()));
                }

                _identicalDuplicates.Add(written.IdenticalDuplicates);
                _conflictingDuplicates.Add(written.ConflictingDuplicates);
                crashes?.Reached(IngestCrashPoint.AfterCommit);

                // Committed: from here a crash only causes redelivery of what is stored, which the Reading key
                // table and the redelivered Rejected message check drop, so finish acknowledging even while stopping.
                foreach (var message in batch)
                {
                    await message.Args.AcknowledgeAsync(CancellationToken.None);
                }

                // Pushed only now, after commit (ADR 0005) and after the PUBACKs (#35), so neither waits on it.
                if (written.AssetIds.Count > 0 || written.RaisedAlertIds.Count > 0 || written.ResolvedAlertIds.Count > 0)
                {
                    _committed.Writer.TryWrite(written);
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

    /// <summary>
    /// The push loop: sends the changes of committed batches to the dashboards, everything queued since the last push
    /// coalesced into one, so a slow push costs pushes, never ingest. <see cref="LivePush"/> logs its own failures.
    /// </summary>
    private async Task PushAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _committed.Reader.WaitToReadAsync(stoppingToken))
            {
                HashSet<Guid> assetIds = [], raisedAlertIds = [], resolvedAlertIds = [];
                while (_committed.Reader.TryRead(out var written))
                {
                    assetIds.UnionWith(written.AssetIds);
                    raisedAlertIds.UnionWith(written.RaisedAlertIds);
                    resolvedAlertIds.UnionWith(written.ResolvedAlertIds);
                }

                await live.PushAsync(assetIds, raisedAlertIds, resolvedAlertIds, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
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
    /// contract. Whether its Device is known and not Archived is decided when the batch is stored. A message gets
    /// the first reason in this order: topic version, then payload, then Device. So a malformed payload from an
    /// unknown or Archived Device is rejected for its payload: the Device check needs a parsed Reading.
    /// </summary>
    private static (List<ReceivedReading> Readings, List<ReceivedRejectedMessage> Rejected) Parse(List<ReceivedMessage> batch)
    {
        var readings = new List<ReceivedReading>(batch.Count);
        var rejected = new List<ReceivedRejectedMessage>();
        foreach (var (args, receivedTime) in batch)
        {
            var message = args.ApplicationMessage;
            var payload = message.Payload.ToArray();
            void Reject(RejectReason reason) => rejected.Add(new(message.Topic, payload, reason, receivedTime, message.Dup));

            var (version, serialNumber) = TopicLevels(message.Topic);
            if (version != ContractVersion)
            {
                Reject(RejectReason.UnknownTopicVersion);
            }
            else if (TelemetryPayload.TryParse(payload, receivedTime, out var reading, out var reason))
            {
                readings.Add(new ReceivedReading(message.Topic, serialNumber, reading, receivedTime, payload, message.Dup));
            }
            else
            {
                Reject(reason);
            }
        }

        return (readings, rejected);
    }

    /// <summary>The version and serial number levels of a topic that <see cref="TopicFilter"/> matched.</summary>
    private static (string Version, string SerialNumber) TopicLevels(string topic)
    {
        // fieldtrack/{version}/devices/{serialNumber}/telemetry
        var levels = topic.Split('/');
        return (levels[1], levels[3]);
    }

    [LoggerMessage(LogLevel.Information, "Ingest connected as {ClientId}, session resumed: {SessionPresent}.")]
    private partial void LogConnected(string clientId, bool sessionPresent);

    [LoggerMessage(LogLevel.Error, "Ingest session failed; reconnecting in {RetryAfter}.")]
    private partial void LogSessionFailed(Exception exception, TimeSpan retryAfter);

    [LoggerMessage(LogLevel.Warning, "Live push fell behind; dropped a committed batch's changes, dashboards catch up when they reload.")]
    private static partial void LogPushDropped(ILogger logger);

    private sealed record ReceivedMessage(MqttApplicationMessageReceivedEventArgs Args, DateTimeOffset ReceivedTime);
}
