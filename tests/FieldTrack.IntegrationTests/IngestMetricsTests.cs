using FieldTrack.Api.Ingest;

namespace FieldTrack.IntegrationTests;

public sealed class IngestMetricsTests(FieldTrackFixture fixture) : AlertTest(fixture)
{
    [Fact]
    public async Task A_batch_reports_its_rate_size_commit_time_receive_to_commit_time_rule_time_Alerts_and_push_time()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        using var messages = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.MessagesInstrument);
        using var batchSizes = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.BatchSizeInstrument);
        using var commits = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.BatchCommitDurationInstrument);
        using var receiveToCommit = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.ReceiveToCommitDurationInstrument);
        using var ruleTimes = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.RuleEvaluationDurationInstrument);
        using var raised = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.AlertsRaisedInstrument);
        using var pushes = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.AlertPushDurationInstrument);
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", Temp(0, t, 108.4));
        await publisher.PublishAsync("TRK-007", Temp(1, t.AddSeconds(1), 109));
        await publisher.PublishAsync("TRK-007", "not json");
        await EventuallyAsync(() => Task.FromResult(messages.Total), total => total == 3);

        // Every message counts, rejected ones too: batches hold messages, and the sizes of all batches add up to them.
        batchSizes.Total.ShouldBe(3);
        batchSizes.Measurements.ShouldAllBe(size => size.Value >= 1);
        // The worker counts a batch's messages last (IngestMetrics.RecordBatch), so its other instruments are complete by now.
        var batches = batchSizes.Measurements.Count;
        commits.Seconds.Count.ShouldBe(batches);
        ruleTimes.Seconds.Count.ShouldBe(batches);
        receiveToCommit.Seconds.Count.ShouldBe(3);
        commits.Seconds.ShouldAllBe(seconds => seconds > 0 && seconds < 10);
        receiveToCommit.Seconds.ShouldAllBe(seconds => seconds >= 0 && seconds < 10);
        raised.Total.ShouldBe(1);
        // The push loop runs after the commit and the acknowledgements, so it is awaited separately.
        await EventuallyAsync(() => Task.FromResult(pushes.Seconds.Count), count => count == 1);
        pushes.Seconds.ShouldAllBe(seconds => seconds >= 0 && seconds < 10);
    }
}
