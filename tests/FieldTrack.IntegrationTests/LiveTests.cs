using System.Net;
using System.Text.Json;
using FieldTrack.Api.Live;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Ingest;
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
        await CreateAsync(admin, "/alert-rules", Overheat(alerting));
        var t = DateTimeOffset.UtcNow.AddSeconds(1);
        // The newest by Device time is the latest, even when an older one is stored after it.
        await WriteAsync(Received("TRK-002", 0, t.AddSeconds(2), 90, lat: 51.6), Received("TRK-003", 0, t, 110));
        await WriteAsync(Received("TRK-002", 1, t.AddSeconds(1), 95, lat: 51.4));

        var assets = (await ListAsync(admin, "/assets/live")).ToDictionary(asset => asset.GetProperty("name").GetString()!);

        assets["Spare generator"].GetProperty("status").GetString().ShouldBe("NoDevice");
        assets["Spare generator"].GetProperty("latest").ValueKind.ShouldBe(JsonValueKind.Null);
        assets["Asset of TRK-001"].GetProperty("status").GetString().ShouldBe("Offline");
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
            var assets = await client.NextAsync(LiveHub.AssetsUpdated);
            var pushed = assets.EnumerateArray().ShouldHaveSingleItem();
            pushed.GetProperty("id").GetString().ShouldBe(asset);
            pushed.GetProperty("status").GetString().ShouldBe("Alerting");
        }
    }

    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    private async Task WriteAsync(params ReceivedReading[] readings)
    {
        await using var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, CancellationToken);
        await writer.WriteAsync(readings, [], CancellationToken);
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
