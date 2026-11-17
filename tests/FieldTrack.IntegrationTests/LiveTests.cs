using System.Net;
using System.Text.Json;
using FieldTrack.Api.Auth;
using FieldTrack.Api.Live;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Ingest;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class LiveTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task The_dashboard_read_lists_Assets_with_Asset_Status_and_their_latest_Reading()
    {
        using var admin = await AdminClientAsync();
        await CreateAsync(admin, "/assets", new { name = "Spare generator", type = "Generator" });
        await AttachedDeviceAsync(admin, "TRK-001");
        await AttachedDeviceAsync(admin, "TRK-002");
        var (_, alerting) = await AttachedDeviceAsync(admin, "TRK-003");
        var (_, archived) = await AttachedDeviceAsync(admin, "TRK-004");
        await CreateAsync(admin, "/alert-rules", Overheat(alerting));
        var t = DateTimeOffset.UtcNow.AddSeconds(1);
        // The newest by Device time is the latest, even when an older one is stored after it.
        await WriteAsync(Received("TRK-002", 0, t.AddSeconds(2), 90, lat: 51.6), Received("TRK-003", 0, t, 110), Received("TRK-004", 0, t, 90));
        await WriteAsync(Received("TRK-002", 1, t.AddSeconds(1), 95, lat: 51.4));
        // An Asset with Readings is Archived, not removed.
        (await admin.DeleteAsync($"/assets/{archived}", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var assets = (await ListAsync(admin, "/assets/live")).ToDictionary(asset => asset.GetProperty("name").GetString()!);

        assets.ShouldNotContainKey("Asset of TRK-004");
        assets["Spare generator"].GetProperty("status").GetString().ShouldBe("NoDevice");
        assets["Spare generator"].GetProperty("latest").ValueKind.ShouldBe(JsonValueKind.Null);
        // Attached moments ago and silent since: its 120 s have not run out (Offline itself is tested in Domain).
        assets["Asset of TRK-001"].GetProperty("status").GetString().ShouldBe("Normal");
        assets["Asset of TRK-001"].GetProperty("lastReceivedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        assets["Asset of TRK-003"].GetProperty("status").GetString().ShouldBe("Alerting");
        var normal = assets["Asset of TRK-002"];
        normal.GetProperty("status").GetString().ShouldBe("Normal");
        normal.GetProperty("lastReceivedAt").GetDateTimeOffset().ShouldBeGreaterThan(t.AddMinutes(-1));
        var latest = normal.GetProperty("latest");
        latest.GetProperty("sequenceNumber").GetInt64().ShouldBe(0);
        latest.GetProperty("deviceSerialNumber").GetString().ShouldBe("TRK-002");
        latest.GetProperty("latitude").GetDouble().ShouldBe(51.6, tolerance: 1e-9);
        latest.GetProperty("longitude").GetDouble().ShouldBe(-0.12, tolerance: 1e-9);
        latest.GetProperty("engineTempC").GetSingle().ShouldBe(90);
        // Same shape and values as the history read's newest Reading.
        var from = Uri.EscapeDataString(t.AddMinutes(-1).ToString("O"));
        var to = Uri.EscapeDataString(t.AddMinutes(1).ToString("O"));
        var history = await ListAsync(admin, $"/assets/{normal.GetProperty("id").GetString()}/readings?from={from}&to={to}");
        latest.GetRawText().ShouldBe(history[^1].GetRawText());
    }

    [Fact]
    public async Task Anyone_signed_in_connects_to_the_hub_with_the_access_token_query_parameter_and_nobody_else()
    {
        using var admin = await AdminClientAsync();
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        var operatorTokens = await AuthClient.SignInAsync(Fixture.Api.CreateClient(), "operator@fieldtrack.test", "Operator-pass-1");

        await using (await LiveClient.ConnectAsync(Fixture.Api, operatorTokens.AccessToken))
        {
        }

        (await Should.ThrowAsync<HttpRequestException>(() => LiveClient.ConnectAsync(Fixture.Api, accessToken: null)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Should.ThrowAsync<HttpRequestException>(() => LiveClient.ConnectAsync(Fixture.Api, "not-a-token")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_Alert_update_reaches_every_dashboard_with_its_Asset_Status()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        await AttachedDeviceAsync(admin, "TRK-008");
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        await WriteAsync(Received("TRK-007", 0, DateTimeOffset.UtcNow.AddSeconds(1), 110));
        var alert = (await ListAsync(admin, "/alerts")).ShouldHaveSingleItem().GetProperty("id").GetString()!;
        await using var first = await LiveClient.ConnectAsync(Fixture.Api, Token(admin));
        await using var second = await LiveClient.ConnectAsync(Fixture.Api, Token(admin));

        // What Acknowledge and Resolve (#31) call once their change commits.
        await Fixture.Api.Services.GetRequiredService<LivePush>().AlertUpdatedAsync(Guid.Parse(alert), CancellationToken);

        foreach (var client in new[] { first, second })
        {
            var updated = await client.NextAsync(LiveHub.AlertUpdated);
            updated.GetProperty("id").GetString().ShouldBe(alert);
            updated.GetProperty("status").GetString().ShouldBe("Open");
            // Only the Alert's Asset, not TRK-008's.
            var assets = await client.NextAsync(LiveHub.AssetsUpdated);
            var pushed = assets.EnumerateArray().ShouldHaveSingleItem();
            pushed.GetProperty("id").GetString().ShouldBe(asset);
            pushed.GetProperty("status").GetString().ShouldBe("Alerting");
        }
    }

    [Fact]
    public async Task A_breach_published_over_MQTT_is_pushed_after_commit_with_the_Asset_and_its_clear_as_an_Alert_update()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        await using var live = await LiveClient.ConnectAsync(api, Token(admin));
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        var t = DateTimeOffset.UtcNow.AddSeconds(1);

        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, t, lat: 51.6, more: ",\"engineTempC\":110"));
        var raised = await live.NextAsync(LiveHub.AlertRaised);
        var breached = (await live.NextAsync(LiveHub.AssetsUpdated)).EnumerateArray().ShouldHaveSingleItem();
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, t.AddSeconds(1), more: ",\"engineTempC\":90"));
        var resolved = await live.NextAsync(LiveHub.AlertUpdated);
        var cleared = (await live.NextAsync(LiveHub.AssetsUpdated)).EnumerateArray().ShouldHaveSingleItem();

        raised.GetProperty("assetId").GetString().ShouldBe(asset);
        raised.GetProperty("status").GetString().ShouldBe("Open");
        breached.GetProperty("id").GetString().ShouldBe(asset);
        breached.GetProperty("status").GetString().ShouldBe("Alerting");
        breached.GetProperty("latest").GetProperty("latitude").GetDouble().ShouldBe(51.6, tolerance: 1e-9);
        resolved.GetProperty("id").GetString().ShouldBe(raised.GetProperty("id").GetString());
        resolved.GetProperty("status").GetString().ShouldBe("Resolved");
        resolved.GetProperty("resolveReason").GetString().ShouldBe("Cleared");
        cleared.GetProperty("status").GetString().ShouldBe("Normal");
        cleared.GetProperty("latest").GetProperty("sequenceNumber").GetInt64().ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ingest_keeps_committing_while_the_push_stalls_or_throws(bool stalled)
    {
        var hub = stalled ? BrokenLiveHub.Stalled() : BrokenLiveHub.Throwing();
        await using var api = await StartIngestAsync(services: services => services.AddSingleton<HubLifetimeManager<LiveHub>>(hub));
        using var admin = await AdminClientAsync(api);
        var (device, _) = await AttachedDeviceAsync(admin, "TRK-011");
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await using var database = await OpenDatabaseAsync();
        var t = DateTimeOffset.UtcNow.AddSeconds(1);

        await publisher.PublishAsync("TRK-011", TelemetryPublisher.Payload(1, 0, t));
        await hub.Sent.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken);
        // A later batch, published while the first push is still stuck or after it failed.
        await publisher.PublishAsync("TRK-011", TelemetryPublisher.Payload(1, 1, t.AddSeconds(1)));

        (await EventuallyAsync(() => SqlAsync(database, "SELECT count(*) FROM readings WHERE device_id = @p0", Guid.Parse(device)), n => n is 2L))
            .ShouldBe(2L);
    }

    [Fact]
    public async Task A_redelivered_batch_changes_nothing_to_push()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-012");
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var t = DateTimeOffset.UtcNow.AddSeconds(1);
        ReceivedReading[] batch = [Received("TRK-012", 0, t, 110), Received("TRK-012", 1, t.AddSeconds(1), 90)];
        (await WriteAsync(batch)).AssetIds.ShouldBe([Guid.Parse(asset)]);

        var redelivered = await WriteAsync([.. batch.Select(reading => reading with { Redelivered = true })]);

        redelivered.AssetIds.ShouldBeEmpty();
        redelivered.RaisedAlertIds.ShouldBeEmpty();
        redelivered.ResolvedAlertIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Alert_raised_and_Resolved_in_one_batch_is_reported_raised_once_and_not_resolved()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-013");
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var t = DateTimeOffset.UtcNow.AddSeconds(1);

        var written = await WriteAsync(Received("TRK-013", 0, t, 110), Received("TRK-013", 1, t.AddSeconds(1), 90));

        var alert = (await ListAsync(admin, "/alerts")).ShouldHaveSingleItem();
        alert.GetProperty("status").GetString().ShouldBe("Resolved");
        written.RaisedAlertIds.ShouldBe([alert.GetProperty("id").GetGuid()]);
        written.ResolvedAlertIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Asset_quiet_for_days_still_shows_its_latest_Reading()
    {
        var time = new AdjustableTimeProvider();
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        using var admin = await AdminClientAsync(api);
        // Attached three days ago, so a Reading from two days ago is stamped with the Asset.
        time.Offset = TimeSpan.FromDays(-3);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-014");
        var t = DateTimeOffset.UtcNow.AddDays(-2);
        (await WriteAsync(Received("TRK-014", 0, t.AddSeconds(-1), 80), Received("TRK-014", 1, t, 85))).AssetIds.ShouldBe([Guid.Parse(asset)]);
        time.Offset = TimeSpan.Zero;

        var live = (await ListAsync(admin, "/assets/live")).Single(a => a.GetProperty("id").GetString() == asset);

        live.GetProperty("latest").GetProperty("sequenceNumber").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task An_Alert_raised_and_Resolved_in_one_commit_goes_as_updated_not_raised()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-009");
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var t = DateTimeOffset.UtcNow.AddSeconds(1);
        await WriteAsync(Received("TRK-009", 0, t, 110));
        await WriteAsync(Received("TRK-009", 1, t.AddSeconds(1), 90));
        var alert = (await ListAsync(admin, "/alerts")).ShouldHaveSingleItem();
        alert.GetProperty("status").GetString().ShouldBe("Resolved");
        var id = Guid.Parse(alert.GetProperty("id").GetString()!);
        await using var client = await LiveClient.ConnectAsync(Fixture.Api, Token(admin));

        await Fixture.Api.Services.GetRequiredService<LivePush>().PushAsync([], [id], [id], CancellationToken);

        var (name, payload) = await client.NextAsync();
        name.ShouldBe(LiveHub.AlertUpdated);
        payload.GetProperty("id").GetGuid().ShouldBe(id);
        (await client.NextAsync()).Name.ShouldBe(LiveHub.AssetsUpdated);
    }

    [Fact]
    public async Task A_failed_push_is_logged_and_never_reaches_the_caller()
    {
        using var admin = await AdminClientAsync();
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-010");
        var hub = BrokenLiveHub.Throwing();
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<HubLifetimeManager<LiveHub>>(hub)));

        await Should.NotThrowAsync(() => api.Services.GetRequiredService<LivePush>().PushAsync([Guid.Parse(asset)], [], [], CancellationToken));
        hub.Sent.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task A_WebSocket_connection_closes_when_its_access_token_expires()
    {
        // Issue a token with 5 s left, then put the clock right so only the token is old.
        var time = new AdjustableTimeProvider { Offset = -(TokenService.AccessTokenLifetime - TimeSpan.FromSeconds(5)) };
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        var tokens = await AuthClient.SignInAsync(api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword);
        time.Offset = TimeSpan.Zero;

        await using var client = await LiveClient.ConnectAsync(api, tokens.AccessToken, webSockets: true);

        client.Closed.IsCompleted.ShouldBeFalse();
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken);
    }

    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    private async Task<WrittenBatch> WriteAsync(params ReceivedReading[] readings)
    {
        await using var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, CancellationToken);
        return await writer.WriteAsync(readings, [], CancellationToken);
    }

    // Device time must come after the Attachment's server-time start for the Reading to be stamped with the Asset.
    private static ReceivedReading Received(string serialNumber, long seq, DateTimeOffset deviceTime, float engineTempC, double lat = 51.5) =>
        new(TelemetryPublisher.TopicOf(serialNumber), serialNumber,
            new Reading(1, seq, DateTimeOffset.FromUnixTimeMilliseconds(deviceTime.ToUnixTimeMilliseconds()), lat, -0.12, null, engineTempC, null, null, null),
            DateTimeOffset.UtcNow, [(byte)seq], Redelivered: false);

    private static object Overheat(string assetId) => new
    {
        name = "Engine overheat",
        kind = "Threshold",
        assetId,
        severity = "Critical",
        sensor = "EngineTempC",
        @operator = "GreaterThan",
        threshold = 105,
        hysteresis = 5,
    };

    private static async Task<(string Device, string Asset)> AttachedDeviceAsync(HttpClient admin, string serialNumber)
    {
        var device = await CreateAsync(admin, "/devices", new { serialNumber });
        var asset = await CreateAsync(admin, "/assets", new { name = $"Asset of {serialNumber}", type = "Truck" });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        return (device, asset);
    }
}
