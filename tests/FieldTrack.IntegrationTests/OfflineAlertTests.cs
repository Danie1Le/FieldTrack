using System.Text.Json;
using FieldTrack.Api.Live;
using Microsoft.Extensions.Time.Testing;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// The ingest worker's 1 s sweep (ADR 0005), driven by a <see cref="FakeTimeProvider"/> stepped one second at a time:
/// each step is one sweep, so the sweep time an Alert records says which tick raised it.
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
        var raised = await StepUntilAsync(time, () => ListAsync(admin, "/alerts"), alerts => alerts.Length > 0);
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

        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var startedAt = time.GetUtcNow();
        await using var api = await StartIngestAsync(time);
        var raised = await StepUntilAsync(time, () => ListAsync(admin, "/alerts"), alerts => alerts.Length > 0);

        // The first sweep, 1 s in, finds no backlog waiting; the rule's 60 s count from there.
        raised.ShouldHaveSingleItem().GetProperty("raisedAt").GetDateTimeOffset().ShouldBeInRange(startedAt.AddSeconds(61), startedAt.AddSeconds(62));
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
        await StepUntilAsync(time, () => Task.FromResult(offline.IsCompleted), completed => completed);

        var pushed = (await offline).EnumerateArray().ShouldHaveSingleItem();
        pushed.GetProperty("id").GetString().ShouldBe(asset);
        pushed.GetProperty("lastReceivedAt").GetDateTimeOffset().ShouldBe(lastReceivedAt);
        time.GetUtcNow().ShouldBeGreaterThanOrEqualTo(lastReceivedAt.AddSeconds(120));
    }

    private static object Silent(string assetId) =>
        new { name = "Generator silent", kind = "Offline", assetId, severity = "Critical", offlineSeconds = 60 };

    private static async Task<DateTimeOffset> LastReceivedAtAsync(HttpClient admin) =>
        (await EventuallyAsync(() => ListAsync(admin, "/devices"), devices => devices.Single().GetProperty("lastReceivedAt").ValueKind != JsonValueKind.Null))
        .Single().GetProperty("lastReceivedAt").GetDateTimeOffset();

    // One fake second per step, so the worker sweeps once per step; the short real pause lets that sweep finish first.
    private static async Task<T> StepUntilAsync<T>(FakeTimeProvider time, Func<Task<T>> read, Func<T, bool> done)
    {
        for (var step = 0; step < 300; step++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, CancellationToken);
            var value = await read();
            if (done(value))
            {
                return value;
            }
        }

        throw new TimeoutException("Nothing happened in 300 fake seconds.");
    }
}
