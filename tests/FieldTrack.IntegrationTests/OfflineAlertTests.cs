using System.Text.Json;
using FieldTrack.Api.Ingest;
using FieldTrack.Api.Live;
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
    public async Task Readings_queued_while_ingest_was_down_hold_off_Offline_Alerts_until_the_backlog_is_stored()
    {
        // More than the 2 x BatchSize messages the broker keeps in flight, so part of the backlog waits in its queue.
        const int PerDevice = 1_200;
        string[] serials = ["GEN-001", "GEN-002"];
        using var admin = await AdminClientAsync();
        foreach (var serial in serials)
        {
            var (_, asset) = await AttachedDeviceAsync(admin, serial);
            await CreateAsync(admin, "/alert-rules", Silent(asset, offlineSeconds: 1));
        }

        // Both Devices have been silent for an hour as far as the database knows: ingest was down.
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, "UPDATE attachments SET attached_at = now() - interval '2 hours'");
        await SqlAsync(database, "UPDATE devices SET last_received_at = now() - interval '1 hour'");
        var clientId = NewIngestClientId();
        await TelemetryPublisher.CreateIngestSessionAsync(Fixture, clientId);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using (var publisher = await TelemetryPublisher.ConnectAsync(Fixture))
        {
            for (var seq = 0; seq < PerDevice; seq++)
            {
                foreach (var serial in serials)
                {
                    await publisher.PublishAsync(serial, TelemetryPublisher.Payload(1, seq, time.GetUtcNow()));
                }
            }
        }

        await using var api = await StartIngestAsync(time, clientId);
        await EventuallyAsync(() => SqlAsync(database, "SELECT count(*) FROM readings"), count => (long)count! > 0);
        // A sweep every fake second while the backlog drains, until each Device has its Offline Alert.
        var alerts = await StepUntilAsync(api, time, async () => (await SqlAsync(database, "SELECT count(*) FROM readings"), await ListAsync(admin, "/alerts")),
            state => (long)state.Item1! == serials.Length * PerDevice && state.Item2.Length == serials.Length);

        // One Alert per Device, never resolved by a backlog Reading and raised again: each came only once its Device's
        // last queued Reading was stored, and the rule's 1 s had passed since.
        foreach (var alert in alerts.Item2)
        {
            alert.GetProperty("status").GetString().ShouldBe("Open");
            var lastReceived = (DateTime)(await SqlAsync(database, "SELECT max(received_time) FROM readings WHERE asset_id = @p0",
                Guid.Parse(alert.GetProperty("assetId").GetString()!)))!;
            alert.GetProperty("raisedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(new DateTimeOffset(lastReceived, TimeSpan.Zero).AddSeconds(1));
        }
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

        // A detach in progress: it has bumped the Alert Rule version, so the sweep that would raise waits on it.
        await using var detach = await OpenDatabaseAsync();
        await using var transaction = await detach.BeginTransactionAsync(CancellationToken);
        await SqlAsync(detach, "UPDATE alert_rule_version SET version = version + 1");
        await SqlAsync(detach, "UPDATE attachments SET detached_at = now()");
        time.Advance(TimeSpan.FromSeconds(1));
        await LockWaitsAsync(1);
        await transaction.CommitAsync(CancellationToken);
        await EventuallyAsync(() => Task.FromResult(sweeps.Total), total => total == 60);
        await StepUntilAsync(api, time, () => Task.FromResult(sweeps.Total), total => total >= 62);

        (await ListAsync(admin, "/alerts")).ShouldBeEmpty();
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
}
