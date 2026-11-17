using System.Diagnostics.Metrics;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Ingest;

namespace FieldTrack.Api.Ingest;

/// <summary>
/// The instruments ingest and its live push report on the <see cref="MeterName"/> meter (spec #21, #48). One type holds
/// them so the worker and <see cref="Live.LivePush"/> share a meter and the recording order stays in one place.
/// </summary>
public sealed class IngestMetrics
{
    /// <summary>The meter ingest reports on.</summary>
    public const string MeterName = "FieldTrack.Ingest";

    /// <summary>The tag on <see cref="RejectedMessagesInstrument"/> holding the <see cref="RejectReason"/> name.</summary>
    public const string ReasonTag = "reason";

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

    /// <summary>The histogram of how long a batch took to store, evaluate the Alert Rules and commit, once parsed.</summary>
    public const string BatchCommitDurationInstrument = "fieldtrack.ingest.batch_commit_duration";

    /// <summary>
    /// The histogram of each message's time from the broker handing it over to its batch committing: the platform's share
    /// of ADR 0004's p95 publish-to-commit target of 200 ms. Publisher and broker time are not in it; the bench (#45)
    /// measures the whole.
    /// </summary>
    public const string ReceiveToCommitDurationInstrument = "fieldtrack.ingest.receive_to_commit_duration";

    /// <summary>The histogram of how long evaluating the Alert Rules took within each batch's transaction.</summary>
    public const string RuleEvaluationDurationInstrument = "fieldtrack.ingest.rule_evaluation_duration";

    /// <summary>The counter of Offline sweeps run (ADR 0005), so a stalled sweep shows.</summary>
    public const string SweepsInstrument = "fieldtrack.ingest.sweeps";

    /// <summary>The counter of Alerts raised by ingest.</summary>
    public const string AlertsRaisedInstrument = "fieldtrack.ingest.alerts_raised";

    /// <summary>
    /// The histogram of the time from an Alert's creation to its <see cref="Live.LiveHub.AlertRaised"/> push being sent:
    /// the server's share of alert latency. The time to the browser is measured end to end by the bench (#45).
    /// </summary>
    public const string AlertPushDurationInstrument = "fieldtrack.ingest.alert_push_duration";

    // Seconds from 1 ms to 10 s, around ADR 0004's 200 ms target; the default boundaries would put every batch in the first bucket.
    private static readonly InstrumentAdvice<double> _durationBuckets = new()
    {
        HistogramBucketBoundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.2, 0.5, 1, 2.5, 5, 10],
    };

    private readonly Counter<long> _messages;
    private readonly Histogram<long> _batchSize;
    private readonly Histogram<double> _batchCommitDuration;
    private readonly Histogram<double> _receiveToCommitDuration;
    private readonly Histogram<double> _ruleEvaluationDuration;
    private readonly Histogram<double> _alertPushDuration;
    private readonly Counter<long> _alertsRaised;
    private readonly Counter<long> _sweeps;
    private readonly Counter<long> _rejectedMessages;
    private readonly Counter<long> _identicalDuplicates;
    private readonly Counter<long> _conflictingDuplicates;

    /// <summary>Creates the instruments on the meter <paramref name="meters"/> hands out.</summary>
    public IngestMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _messages = meter.CreateCounter<long>(MessagesInstrument, "{message}", "Telemetry messages processed, stored or rejected.");
        _batchSize = meter.CreateHistogram<long>(
            BatchSizeInstrument, "{message}", "Messages per ingest batch.",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000] });
        _batchCommitDuration = meter.CreateHistogram(
            BatchCommitDurationInstrument, "s", "Time to store a parsed batch, evaluate the Alert Rules and commit.", advice: _durationBuckets);
        _receiveToCommitDuration = meter.CreateHistogram(
            ReceiveToCommitDurationInstrument, "s", "Time from a message's receipt to its batch's commit.", advice: _durationBuckets);
        _ruleEvaluationDuration = meter.CreateHistogram(
            RuleEvaluationDurationInstrument, "s", "Time to evaluate the Alert Rules within a batch's transaction.", advice: _durationBuckets);
        _alertPushDuration = meter.CreateHistogram(
            AlertPushDurationInstrument, "s", "Time from an Alert's creation to its push being sent.", advice: _durationBuckets);
        _alertsRaised = meter.CreateCounter<long>(AlertsRaisedInstrument, "{alert}", "Alerts raised by ingest.");
        _sweeps = meter.CreateCounter<long>(SweepsInstrument, "{sweep}", "Offline sweeps run.");
        _rejectedMessages = meter.CreateCounter<long>(
            RejectedMessagesInstrument, "{message}", "Telemetry messages stored as Rejected messages, by reason.");
        _identicalDuplicates = meter.CreateCounter<long>(
            IdenticalDuplicatesInstrument, "{reading}", "Copies of stored Readings dropped with the same content hash.");
        _conflictingDuplicates = meter.CreateCounter<long>(
            ConflictingDuplicatesInstrument, "{reading}", "Copies of stored Readings dropped with a different content hash.");
    }

    /// <summary>
    /// Records a committed batch: when each of its messages was received, how long the batch took from parsed to
    /// committed, and what it wrote. The message counter goes last, so once it shows a batch's messages every other
    /// instrument holds that batch too.
    /// </summary>
    public void RecordBatch(IReadOnlyList<DateTimeOffset> receivedTimes, DateTimeOffset committed, TimeSpan elapsed, WrittenBatch written)
    {
        _batchSize.Record(receivedTimes.Count);
        _batchCommitDuration.Record(elapsed.TotalSeconds);
        _ruleEvaluationDuration.Record(written.RuleEvaluation.TotalSeconds);
        foreach (var received in receivedTimes)
        {
            // A wall-clock step between receipt and commit can make the difference negative.
            _receiveToCommitDuration.Record(Math.Max(0, (committed - received).TotalSeconds));
        }

        _alertsRaised.Add(written.RaisedAlertIds.Count);
        foreach (var reason in written.Rejected)
        {
            _rejectedMessages.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason.ToString()));
        }

        _identicalDuplicates.Add(written.IdenticalDuplicates);
        _conflictingDuplicates.Add(written.ConflictingDuplicates);
        _messages.Add(receivedTimes.Count);
    }

    /// <summary>Records a committed sweep and the Offline Alerts it raised; the sweep counter goes last, as in <see cref="RecordBatch"/>.</summary>
    public void RecordSweep(WrittenBatch written)
    {
        _alertsRaised.Add(written.RaisedAlertIds.Count);
        _sweeps.Add(1);
    }

    /// <summary>Records that an Alert's push went out <paramref name="sinceCreated"/> after the platform created it.</summary>
    public void RecordAlertPush(TimeSpan sinceCreated) =>
        // A wall-clock step between creation and push can make the difference negative.
        _alertPushDuration.Record(Math.Max(0, sinceCreated.TotalSeconds));
}
