using System.Text.Json;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class ThresholdAlertTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task A_breach_published_over_MQTT_raises_an_Alert_and_a_clear_past_the_Hysteresis_resolves_it()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (device, asset) = await AttachedDeviceAsync(admin);
        // Created while the worker runs: ingest picks up rules on its next batch.
        var rule = await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", Temp(0, t, 108.4));
        var raised = await AlertsWhenAsync(admin, alerts => alerts.Length == 1);
        await publisher.PublishAsync("TRK-007", Temp(1, t.AddSeconds(1), 101));
        await publisher.PublishAsync("TRK-007", Temp(2, t.AddSeconds(2), 99.5));
        var resolved = await AlertsWhenAsync(admin, alerts => alerts.Any(alert => alert.GetProperty("status").GetString() == "Resolved"));

        var open = raised.Single();
        open.GetProperty("status").GetString().ShouldBe("Open");
        open.GetProperty("alertRuleId").GetString().ShouldBe(rule);
        open.GetProperty("assetId").GetString().ShouldBe(asset);
        open.GetProperty("reading").GetProperty("deviceId").GetString().ShouldBe(device);
        open.GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(0);
        open.GetProperty("severity").GetString().ShouldBe("Critical");
        open.GetProperty("message").GetString().ShouldBe("Engine temp 108.4 °C, above 105 (clears below 100)");
        open.GetProperty("raisedAt").GetDateTimeOffset().ShouldBe(Millis(t));
        open.GetProperty("createdAt").GetDateTimeOffset().ShouldBeGreaterThan(t.AddMinutes(-1));
        var closed = resolved.Single();
        closed.GetProperty("id").GetString().ShouldBe(open.GetProperty("id").GetString());
        closed.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBe(Millis(t.AddSeconds(2)));
        closed.GetProperty("resolveReason").GetString().ShouldBe("Cleared");
        closed.GetProperty("fromBacklog").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Rule_state_survives_a_worker_restart_so_a_breach_that_still_holds_raises_nothing_new()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await using (await StartIngestAsync())
        {
            await publisher.PublishAsync("TRK-007", Temp(0, t, 110));
            await AlertsWhenAsync(admin, alerts => alerts.Length == 1);
        }

        await using var restarted = await StartIngestAsync();
        // Still breached, which raises only if the restarted worker forgot the state, then a clear.
        await publisher.PublishAsync("TRK-007", Temp(1, t.AddSeconds(1), 112));
        await publisher.PublishAsync("TRK-007", Temp(2, t.AddSeconds(2), 90));
        var alerts = await AlertsWhenAsync(admin, alerts => alerts.Any(alert => alert.GetProperty("status").GetString() == "Resolved"));

        var alert = alerts.ShouldHaveSingleItem();
        alert.GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(0);
        alert.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBe(Millis(t.AddSeconds(2)));
    }

    [Fact]
    public async Task A_rule_on_an_Asset_Group_raises_one_Alert_per_Asset_and_disabled_rules_raise_nothing()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var group = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });
        var (_, first) = await AttachedDeviceAsync(admin, "TRK-007", group);
        var (_, second) = await AttachedDeviceAsync(admin, "TRK-008", group);
        var (_, outside) = await AttachedDeviceAsync(admin, "TRK-009");
        await CreateAsync(admin, "/alert-rules", Overheat(assetGroupId: group));
        await CreateAsync(admin, "/alert-rules", Overheat(outside) with { enabled = false });
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        foreach (var serialNumber in new[] { "TRK-009", "TRK-007", "TRK-008" })
        {
            await publisher.PublishAsync(serialNumber, Temp(0, t, 120));
            await publisher.PublishAsync(serialNumber, Temp(1, t.AddSeconds(1), 121));
        }

        var alerts = await AlertsWhenAsync(admin, alerts => alerts.Length >= 2);

        alerts.Select(alert => alert.GetProperty("assetId").GetString()).Order().ShouldBe(new[] { first, second }.Order());
    }

    [Fact]
    public async Task A_backlog_breach_that_clears_inside_one_batch_is_stored_raised_and_resolved_from_backlog()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin);
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var t = DateTimeOffset.UtcNow;

        // One batch, as a Coverage gap's backlog arrives, and not in Sequence number order.
        await using (var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, CancellationToken))
        {
            await writer.WriteAsync([Received(2, t.AddSeconds(2), 95), Received(0, t, 90), Received(1, t.AddSeconds(1), 107)], CancellationToken);
        }

        var alert = (await ListAsync(admin, "/alerts")).ShouldHaveSingleItem();
        alert.GetProperty("status").GetString().ShouldBe("Resolved");
        alert.GetProperty("raisedAt").GetDateTimeOffset().ShouldBe(Millis(t.AddSeconds(1)));
        alert.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBe(Millis(t.AddSeconds(2)));
        alert.GetProperty("resolveReason").GetString().ShouldBe("Cleared");
        alert.GetProperty("fromBacklog").GetBoolean().ShouldBeTrue();
    }

    private static ReceivedReading Received(long seq, DateTimeOffset deviceTime, float engineTempC) =>
        new("TRK-007", new Reading(1, seq, Millis(deviceTime), 51.5, -0.12, null, engineTempC, null, null, null), DateTimeOffset.UtcNow, [(byte)seq]);

    private static string Temp(long seq, DateTimeOffset deviceTime, double engineTempC) =>
        TelemetryPublisher.Payload(1, seq, deviceTime, more: FormattableString.Invariant($",\"engineTempC\":{engineTempC}"));

    private static DateTimeOffset Millis(DateTimeOffset time) => DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

#pragma warning disable IDE1006 // JSON property names
    private sealed record RuleRequest(
        string name, string kind, string? assetId, string? assetGroupId, string severity, bool enabled,
        string sensor, string @operator, double threshold, double hysteresis);
#pragma warning restore IDE1006

    private static RuleRequest Overheat(string? assetId = null, string? assetGroupId = null) =>
        new("Engine overheat", "Threshold", assetId, assetGroupId, "Critical", true, "EngineTempC", "GreaterThan", 105, 5);

    private static async Task<JsonElement[]> AlertsWhenAsync(HttpClient admin, Func<JsonElement[], bool> done)
    {
        var alerts = await EventuallyAsync(() => ListAsync(admin, "/alerts"), done);
        done(alerts).ShouldBeTrue();
        return alerts;
    }

    private static async Task<(string Device, string Asset)> AttachedDeviceAsync(HttpClient admin, string serialNumber = "TRK-007", string? assetGroupId = null)
    {
        var device = await CreateAsync(admin, "/devices", new { serialNumber });
        var asset = await CreateAsync(admin, "/assets", new { name = $"Asset of {serialNumber}", type = "Truck", assetGroupId });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        return (device, asset);
    }
}
