using System.Text.Json;
using FieldTrack.Api.Ingest;
using FieldTrack.Api.Live;
using FieldTrack.Infrastructure.Ingest;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// The ingest worker's 1 s sweep (ADR 0005), driven by a <see cref="FakeTimeProvider"/> stepped one second at a time:
/// each step waits for its sweep, so the sweep time an Alert records says which tick raised it.
/// </summary>
public sealed class OfflineAlertTests(FieldTrackFixture fixture) : AlertTest(fixture)
{
    [Fact]
    public async Task An_Offline_rule_raises_within_a_second_of_its_seconds_of_silence_and_the_next_Reading_resolves_it()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await StartIngestAsync(time);
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin);
        var rule = await CreateAsync(admin, "/alert-rules", Silent(asset));
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, time.GetUtcNow()));
        var lastReceivedAt = await LastReceivedAtAsync(admin);
        var raised = await StepUntilAsync(api, time, () => ListAsync(admin, "/alerts"), alerts => alerts.Length > 0);
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, time.GetUtcNow()));
        var resolved = await EventuallyAsync(() => ListAsync(admin, "/alerts"), alerts => alerts.Single().GetProperty("status").GetString() == "Resolved");

        var open = raised.ShouldHaveSingleItem();
        open.GetProperty("alertRuleId").GetString().ShouldBe(rule);
        open.GetProperty("assetId").GetString().ShouldBe(asset);
        open.GetProperty("reading").ValueKind.ShouldBe(JsonValueKind.Null);
        open.GetProperty("message").GetString().ShouldBe("No Reading received for 60 s");
        open.GetProperty("status").GetString().ShouldBe("Open");
        open.GetProperty("raisedAt").GetDateTimeOffset().ShouldBeInRange(lastReceivedAt.AddSeconds(60), lastReceivedAt.AddSeconds(61));
        var closed = resolved.Single();
        closed.GetProperty("resolveReason").GetString().ShouldBe("Cleared");
        closed.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(open.GetProperty("raisedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task After_a_restart_a_Device_silent_for_an_hour_gets_the_rules_seconds_from_when_ingest_caught_up()
    {
        using var admin = await AdminClientAsync();
        var (device, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Silent(asset));
        await using (var database = await OpenDatabaseAsync())
        {
            await SqlAsync(database, "UPDATE attachments SET attached_at = now() - interval '2 hours'");
            await SqlAsync(database, "UPDATE devices SET last_received_at = now() - interval '1 hour' WHERE id = @p0", Guid.Parse(device));
        }

        // Whole seconds, so the database's microseconds lose nothing.
        var startedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var time = new FakeTimeProvider(startedAt);
        await using var api = await StartIngestAsync(time);
        await ConnectedAsync();
        var raised = await StepUntilAsync(api, time, () => ListAsync(admin, "/alerts"), alerts => alerts.Length > 0);

        // The empty broker queue comes back at once, at the start; the rule's 60 s count from there.
        raised.ShouldHaveSingleItem().GetProperty("raisedAt").GetDateTimeOffset().ShouldBeInRange(startedAt.AddSeconds(60), startedAt.AddSeconds(61));
    }

    [Fact]
    public async Task A_sweep_while_the_queued_backlog_is_still_draining_raises_nothing_for_a_silent_Device()
    {
        // A Device silent for an hour as far as the database knows (ingest was down), with a 1 s rule.
        using var admin = await AdminClientAsync();
        var (device, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Silent(asset, offlineSeconds: 1));
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, "UPDATE attachments SET attached_at = now() - interval '2 hours'");
        await SqlAsync(database, "UPDATE devices SET last_received_at = now() - interval '1 hour' WHERE id = @p0", Guid.Parse(device));
        // More than the 2 x BatchSize messages the broker keeps in flight queued meanwhile, so this session's caught-up
        // marker waits behind them in the broker.
        var clientId = NewIngestClientId();
        await TelemetryPublisher.CreateIngestSessionAsync(Fixture, clientId);
        await using (var publisher = await TelemetryPublisher.ConnectAsync(Fixture))
        {
            for (var seq = 0; seq < 2_400; seq++)
            {
                await publisher.PublishAsync("BACKLOG", TelemetryPublisher.Payload(1, seq, DateTimeOffset.UtcNow));
            }
        }

        var startedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var time = new FakeTimeProvider(startedAt);
        using var gate = new BatchGate();
        await using var api = await StartIngestAsync(time, clientId, gate);
        using var sweeps = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.SweepsInstrument);
        // The first backlog batch commits at the start; the second is held while a sweep falls due 10 s later.
        await gate.HeldAsync();
        gate.Release();
        await gate.HeldAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        gate.Open();
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total >= 1);

        // Grace ends only when the marker behind the backlog is stored, never at the first batch, so that sweep raised
        // nothing; the rule's 1 s count from when the backlog was in.
        (await ListAsync(admin, "/alerts")).ShouldBeEmpty();
        var raised = await StepUntilAsync(api, time, () => ListAsync(admin, "/alerts"), alerts => alerts.Length > 0);
        raised.ShouldHaveSingleItem().GetProperty("raisedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(startedAt.AddSeconds(11));
    }

    [Fact]
    public async Task A_detach_that_commits_while_a_sweep_waits_to_raise_leaves_no_Offline_Alert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await StartIngestAsync(time);
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Silent(asset));
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, time.GetUtcNow()));
        await LastReceivedAtAsync(admin);
        using var sweeps = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.SweepsInstrument);
        await StepUntilAsync(api, time, () => Task.FromResult(sweeps.Total), total => total == 59);

        // A detach holds the Asset, so the sweep that would raise has read the Attachment as open and then waits to lock
        // the Asset; meanwhile the detach bumps the Alert Rule version, ends the Attachment and commits.
        await using var detach = await OpenDatabaseAsync();
        await using var transaction = await detach.BeginTransactionAsync(CancellationToken);
        await SqlAsync(detach, "SELECT id FROM assets WHERE id = @p0 FOR UPDATE", Guid.Parse(asset));
        time.Advance(TimeSpan.FromSeconds(1));
        await LockWaitsAsync(1);
        await SqlAsync(detach, "UPDATE alert_rule_version SET version = version + 1");
        await SqlAsync(detach, "UPDATE attachments SET detached_at = now()");
        await transaction.CommitAsync(CancellationToken);
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total == 60);
        await StepUntilAsync(api, time, () => Task.FromResult(sweeps.Total), total => total >= 62);

        // The version moved under the sweep's lock, so it raised nothing; later sweeps see the Asset detached.
        (await ListAsync(admin, "/alerts")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sweep_stores_a_Reading_already_received_before_it_judges_silence()
    {
        var startedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var time = new FakeTimeProvider(startedAt);
        using var gate = new BatchGate { Holding = false };
        await using var api = await StartIngestAsync(time, crashes: gate);
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Silent(asset));
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, time.GetUtcNow()));
        await LastReceivedAtAsync(admin);
        await StepUntilAsync(api, time, () => Task.FromResult(time.GetUtcNow()), now => now == startedAt.AddSeconds(59));

        // A batch is held while the Device's next Reading arrives, 59 s into its 60 s; then the sweep at 60 s falls due.
        gate.Holding = true;
        await publisher.PublishAsync("NOBODY", TelemetryPublisher.Payload(1, 0, time.GetUtcNow()));
        await gate.HeldAsync();
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, time.GetUtcNow()));
        // shortcut: the worker has no signal for "received"; a second covers the broker's hand-over on a busy machine.
        await Task.Delay(1_000, CancellationToken);
        using var sweeps = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.SweepsInstrument);
        time.Advance(TimeSpan.FromSeconds(1));
        gate.Open();
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total == 1);

        // The waiting Reading was stored before the sweep, so the Device was never silent for 60 s: no Alert, not even
        // one raised and then resolved by that Reading.
        (await ListAsync(admin, "/alerts")).ShouldBeEmpty();
        (await LastReceivedAtAsync(admin)).ShouldBe(startedAt.AddSeconds(59));
    }

    [Fact]
    public async Task Sweeps_keep_a_fixed_rate_from_their_due_time_and_skip_ahead_when_a_whole_interval_behind()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var api = await StartIngestAsync(time);
        await ConnectedAsync();
        using var sweeps = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.SweepsInstrument);

        // Due at 1 s, run late at 1.5 s: the next is still due at 2 s, not 2.5 s.
        time.Advance(TimeSpan.FromSeconds(1.5));
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total == 1);
        time.Advance(TimeSpan.FromSeconds(0.5));
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total == 2);
        // Five intervals behind: one sweep, not a burst of five.
        time.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total >= 3);
        time.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total >= 4);

        sweeps.Total.ShouldBe(4);
    }

    [Fact]
    public async Task An_Asset_that_turns_Offline_while_the_ingest_session_restarts_is_still_pushed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var clientId = NewIngestClientId();
        await using var api = await StartIngestAsync(time, clientId);
        using var admin = await AdminClientAsync(api);
        await AttachedDeviceAsync(admin);
        await using var live = await LiveClient.ConnectAsync(api, admin.DefaultRequestHeaders.Authorization!.Parameter!);
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, time.GetUtcNow()));
        var lastReceivedAt = await LastReceivedAtAsync(admin);
        await StepUntilAsync(api, time, () => Task.FromResult(time.GetUtcNow()), now => now == lastReceivedAt.AddSeconds(119));
        var offline = live.NextAsync(LiveHub.AssetsUpdated, assets => assets.EnumerateArray().Any(a => a.GetProperty("status").GetString() == "Offline"));

        // The session drops 1 s before the Asset turns Offline (120 s) and reconnects after it, with no sweep between.
        await TelemetryPublisher.TakeOverIngestSessionAsync(Fixture, clientId);
        await publisher.PublishAsync("NOBODY", TelemetryPublisher.Payload(1, 0, time.GetUtcNow()));
        await using var database = await OpenDatabaseAsync();
        for (var tries = 0; (long)(await SqlAsync(database, "SELECT count(*) FROM rejected_messages"))! == 0; tries++)
        {
            tries.ShouldBeLessThan(60, "the worker never reconnected");
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(500, CancellationToken);
        }

        // The first sweep of the new session covers everything since the old session's last sweep.
        await StepUntilAsync(api, time, () => Task.FromResult(offline.IsCompleted), completed => completed);
        (await offline).EnumerateArray().ShouldHaveSingleItem().GetProperty("status").GetString().ShouldBe("Offline");
    }
    [Fact]
    public async Task The_sweep_pushes_an_Asset_whose_Device_has_been_silent_for_120_seconds_as_Offline()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await StartIngestAsync(time);
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin);
        await using var live = await LiveClient.ConnectAsync(api, admin.DefaultRequestHeaders.Authorization!.Parameter!);
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, time.GetUtcNow()));
        var lastReceivedAt = await LastReceivedAtAsync(admin);

        var offline = live.NextAsync(LiveHub.AssetsUpdated, assets => assets.EnumerateArray().Any(a => a.GetProperty("status").GetString() == "Offline"));
        await StepUntilAsync(api, time, () => Task.FromResult(offline.IsCompleted), completed => completed);

        var pushed = (await offline).EnumerateArray().ShouldHaveSingleItem();
        pushed.GetProperty("id").GetString().ShouldBe(asset);
        pushed.GetProperty("lastReceivedAt").GetDateTimeOffset().ShouldBe(lastReceivedAt);
        time.GetUtcNow().ShouldBeGreaterThanOrEqualTo(lastReceivedAt.AddSeconds(120));
    }

    private static async Task<DateTimeOffset> LastReceivedAtAsync(HttpClient admin) =>
        (await EventuallyAsync(() => ListAsync(admin, "/devices"), devices => devices.Single().GetProperty("lastReceivedAt").ValueKind != JsonValueKind.Null))
        .Single().GetProperty("lastReceivedAt").GetDateTimeOffset();

    // A message from an unknown Device is stored as a Rejected message once the worker is connected and storing.
    private async Task ConnectedAsync()
    {
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await publisher.PublishAsync("NOBODY", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        await using var database = await OpenDatabaseAsync();
        (await EventuallyAsync(() => SqlAsync(database, "SELECT count(*) FROM rejected_messages"), count => (long)count! > 0)).ShouldBe(1L);
    }

    // One fake second per step, each waiting for the worker's sweep at that second (the sweeps counter), so no step
    // overtakes a slow sweep. Call it once the worker is connected: it sweeps only from then.
    private static async Task<T> StepUntilAsync<T>(WebApplicationFactory<Program> api, FakeTimeProvider time, Func<Task<T>> read, Func<T, bool> done)
    {
        using var sweeps = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.SweepsInstrument);
        for (var step = 1; step <= 300; step++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            (await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total >= step)).ShouldBe(step);
            var value = await read();
            if (done(value))
            {
                return value;
            }
        }

        throw new TimeoutException("Nothing happened in 300 fake seconds.");
    }

    // Holds each batch at AfterStage, inside its transaction before the store, until the test lets it go.
    private sealed class BatchGate : IIngestCrashPoints, IDisposable
    {
        private readonly SemaphoreSlim _held = new(0);
        private readonly SemaphoreSlim _go = new(0);
        private volatile bool _holding = true;

        public bool Holding
        {
            get => _holding;
            set => _holding = value;
        }

        public void Reached(IngestCrashPoint point)
        {
            if (point != IngestCrashPoint.AfterStage || !_holding)
            {
                return;
            }

            _held.Release();
            _go.Wait(TimeSpan.FromMinutes(2));
        }

        public async Task HeldAsync() => (await _held.WaitAsync(TimeSpan.FromMinutes(1))).ShouldBeTrue("no batch reached the gate");

        public void Release() => _go.Release();

        // Stops holding and lets a held batch go.
        public void Open()
        {
            _holding = false;
            _go.Release();
        }

        public void Dispose()
        {
            _held.Dispose();
            _go.Dispose();
        }
    }
}
