using System.Net.Http.Json;
using System.Text.Json;

namespace FieldTrack.IntegrationTests;

public sealed class GeofenceAlertTests(FieldTrackFixture fixture) : AlertTest(fixture)
{
    // The depot is a 0.01° square; one position inside it and one just north of it.
    private const double InsideLat = 51.505;
    private const double OutsideLat = 51.52;
    private const double Lon = -0.125;

    [Fact]
    public async Task An_Asset_driven_across_the_boundary_over_MQTT_raises_an_exit_Alert_only_after_it_was_seen_inside_and_resolves_it_when_back()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (device, asset) = await AttachedDeviceAsync(admin);
        var geofence = await CreateAsync(admin, "/geofences", Depot(-0.13));
        var rule = await CreateAsync(admin, "/alert-rules", Rule("GeofenceExit", geofence, assetId: asset));
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        // Outside first: only the baseline. Then in, out (exit) and back in.
        await publisher.PublishAsync("TRK-007", At(0, t, OutsideLat));
        await publisher.PublishAsync("TRK-007", At(1, t.AddSeconds(1), InsideLat));
        await publisher.PublishAsync("TRK-007", At(2, t.AddSeconds(2), OutsideLat));
        var raised = await AlertsWhenAsync(admin, alerts => alerts.Length > 0);
        await publisher.PublishAsync("TRK-007", At(3, t.AddSeconds(3), InsideLat));
        var resolved = await AlertsWhenAsync(admin, alerts => alerts.Any(alert => alert.GetProperty("status").GetString() == "Resolved"));

        var open = raised.ShouldHaveSingleItem();
        open.GetProperty("alertRuleId").GetString().ShouldBe(rule);
        open.GetProperty("assetId").GetString().ShouldBe(asset);
        open.GetProperty("reading").GetProperty("deviceId").GetString().ShouldBe(device);
        open.GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(2);
        open.GetProperty("message").GetString().ShouldBe("Left Geofence Depot");
        open.GetProperty("severity").GetString().ShouldBe("Warning");
        open.GetProperty("raisedAt").GetDateTimeOffset().ShouldBe(TelemetryPublisher.MillisUp(t.AddSeconds(2)));
        var closed = resolved.ShouldHaveSingleItem();
        closed.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBe(TelemetryPublisher.MillisUp(t.AddSeconds(3)));
        closed.GetProperty("resolveReason").GetString().ShouldBe("Cleared");
    }

    [Fact]
    public async Task An_enter_rule_on_an_Asset_Group_raises_one_Alert_per_Asset_that_comes_in_from_outside()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var group = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });
        var (_, first) = await AttachedDeviceAsync(admin, "TRK-007", group);
        var (_, second) = await AttachedDeviceAsync(admin, "TRK-008", group);
        var geofence = await CreateAsync(admin, "/geofences", Depot(-0.13));
        await CreateAsync(admin, "/alert-rules", Rule("GeofenceEnter", geofence, assetGroupId: group));
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        foreach (var serialNumber in new[] { "TRK-007", "TRK-008" })
        {
            await publisher.PublishAsync(serialNumber, At(0, t, OutsideLat));
            await publisher.PublishAsync(serialNumber, At(1, t.AddSeconds(1), InsideLat));
            await publisher.PublishAsync(serialNumber, At(2, t.AddSeconds(2), InsideLat));
        }

        var alerts = await AlertsWhenAsync(admin, alerts => alerts.Length >= 2);

        alerts.Select(alert => alert.GetProperty("assetId").GetString()).Order().ShouldBe(new[] { first, second }.Order());
        alerts.ShouldAllBe(alert => alert.GetProperty("message").GetString() == "Entered Geofence Depot");
    }

    [Fact]
    public async Task A_moved_Geofence_takes_effect_on_the_running_workers_next_Reading_from_a_fresh_baseline()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin);
        var geofence = await CreateAsync(admin, "/geofences", Depot(-0.13));
        await CreateAsync(admin, "/alert-rules", Rule("GeofenceExit", geofence, assetId: asset));
        var t = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await publisher.PublishAsync("TRK-007", At(0, t, InsideLat));

        // Moved 1° east, so the Asset is now outside: a fresh baseline, not an exit.
        using (var moved = await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot(0.87), CancellationToken))
        {
            moved.EnsureSuccessStatusCode();
        }

        await publisher.PublishAsync("TRK-007", At(1, t.AddSeconds(1), InsideLat));
        // Inside the moved depot, which the old area would take for an exit; then out of it.
        await publisher.PublishAsync("TRK-007", At(2, t.AddSeconds(2), InsideLat, lon: 0.875));
        await publisher.PublishAsync("TRK-007", At(3, t.AddSeconds(3), OutsideLat, lon: 0.875));
        var alerts = await AlertsWhenAsync(admin, alerts => alerts.Length > 0);

        alerts.ShouldHaveSingleItem().GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(3);
    }

#pragma warning disable IDE1006 // JSON property names
    private sealed record GeofenceRuleRequest(string name, string kind, string? assetId, string? assetGroupId, string severity, bool enabled, string geofenceId);
#pragma warning restore IDE1006

    // A 0.01° square with its south-west corner at (west, 51.5).
    private static object Depot(double west) => new
    {
        name = "Depot",
        kind = "Inclusion",
        area = new { type = "Polygon", coordinates = new[] { new[] { new[] { west, 51.5 }, [west + 0.01, 51.5], [west + 0.01, 51.51], [west, 51.51], [west, 51.5] } } },
    };

    private static GeofenceRuleRequest Rule(string kind, string geofence, string? assetId = null, string? assetGroupId = null) =>
        new("Depot rule", kind, assetId, assetGroupId, "Warning", true, geofence);

    private static string At(long seq, DateTimeOffset deviceTime, double lat, double lon = Lon) => TelemetryPublisher.Payload(1, seq, deviceTime, lat, lon);

    private static async Task<JsonElement[]> AlertsWhenAsync(HttpClient admin, Func<JsonElement[], bool> done)
    {
        var alerts = await EventuallyAsync(() => ListAsync(admin, "/alerts"), done);
        done(alerts).ShouldBeTrue();
        return alerts;
    }
}
