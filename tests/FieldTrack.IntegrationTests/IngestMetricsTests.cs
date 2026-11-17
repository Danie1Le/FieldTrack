using FieldTrack.Api.Ingest;

namespace FieldTrack.IntegrationTests;

public sealed class IngestMetricsTests(FieldTrackFixture fixture) : AlertTest(fixture)
{
    [Fact]
    public async Task A_batch_reports_its_rate_size_commit_time_receive_to_commit_time_rule_time_and_Alerts()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        using var messages = new CounterRecorder(api.Services, IngestWorker.MeterName, IngestWorker.MessagesInstrument);
        using var batchSizes = new CounterRecorder(api.Services, IngestWorker.MeterName, IngestWorker.BatchSizeInstrument);
        using var commits = new CounterRecorder(api.Services, IngestWorker.MeterName, IngestWorker.BatchCommitDurationInstrument);
        using var receiveToCommit = new CounterRecorder(api.Services, IngestWorker.MeterName, IngestWorker.ReceiveToCommitDurationInstrument);
        using var ruleTimes = new CounterRecorder(api.Services, IngestWorker.MeterName, IngestWorker.RuleEvaluationDurationInstrument);
        using var raised = new CounterRecorder(api.Services, IngestWorker.MeterName, IngestWorker.AlertsRaisedInstrument);
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", Temp(0, t, 108.4));
        await publisher.PublishAsync("TRK-007", Temp(1, t.AddSeconds(1), 109));
        await publisher.PublishAsync("TRK-007", "not json");
        await EventuallyAsync(() => Task.FromResult(messages.Total), total => total == 3);

        // Every message counts, rejected ones too: batches hold messages, and the sizes of all batches add up to them.
        batchSizes.Total.ShouldBe(3);
        batchSizes.Measurements.ShouldAllBe(size => size.Value >= 1);
        // Recorded after commit, per message, so a batch's histograms are complete once the messages are.
        var batches = batchSizes.Measurements.Count;
        commits.Seconds.Count.ShouldBe(batches);
        ruleTimes.Seconds.Count.ShouldBe(batches);
        receiveToCommit.Seconds.Count.ShouldBe(3);
        commits.Seconds.ShouldAllBe(seconds => seconds > 0 && seconds < 10);
        receiveToCommit.Seconds.ShouldAllBe(seconds => seconds >= 0 && seconds < 10);
        raised.Total.ShouldBe(1);
    }
}
