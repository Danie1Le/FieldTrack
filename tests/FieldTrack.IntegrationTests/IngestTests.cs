using System.Globalization;
using System.Net;
using System.Text.Json;

namespace FieldTrack.IntegrationTests;

public sealed class IngestTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task A_published_Reading_is_stored_and_the_Asset_history_returns_it_in_Device_time_order()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        // After the Attachment started, or the Readings would belong to no Asset.
        var t = DateTimeOffset.UtcNow;
        await using var device = await TelemetryPublisher.ConnectAsync(Fixture);

        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 2, t.AddSeconds(2), 51.5, -0.12));
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, t, -33.865143, 151.2099,
            ""","speedKph":61.5,"engineTempC":92.25,"fuelPct":48,"vibrationMmS":3.5,"batteryV":12.75,"firmware":"1.4.2" """));
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, t.AddSeconds(1), more: ""","fuelPct":47.5"""));
        var history = await HistoryWhenAsync(admin, asset, count: 3);

        history.Select(reading => reading.GetProperty("sequenceNumber").GetInt64()).ShouldBe([0L, 1L, 2L]);
        var first = history[0];
        first.GetProperty("deviceSerialNumber").GetString().ShouldBe("TRK-007");
        first.GetProperty("boot").GetInt32().ShouldBe(1);
        first.GetProperty("deviceTime").GetDateTimeOffset().ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds()));
        first.GetProperty("latitude").GetDouble().ShouldBe(-33.865143, tolerance: 1e-9);
        first.GetProperty("longitude").GetDouble().ShouldBe(151.2099, tolerance: 1e-9);
        first.GetProperty("speedKph").GetSingle().ShouldBe(61.5f);
        first.GetProperty("engineTempC").GetSingle().ShouldBe(92.25f);
        first.GetProperty("fuelPct").GetSingle().ShouldBe(48f);
        first.GetProperty("vibrationMmS").GetSingle().ShouldBe(3.5f);
        first.GetProperty("batteryV").GetSingle().ShouldBe(12.75f);
        history[1].GetProperty("speedKph").ValueKind.ShouldBe(JsonValueKind.Null);
        history[1].GetProperty("fuelPct").GetSingle().ShouldBe(47.5f);
        history.ShouldAllBe(reading => reading.GetProperty("receivedTime").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Ingest_records_when_it_last_received_from_each_Device()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        await AttachedDeviceAsync(admin, "TRK-007");
        await CreateAsync(admin, "/devices", new { serialNumber = "GEN-001" });
        await using var device = await TelemetryPublisher.ConnectAsync(Fixture);
        var before = DateTimeOffset.UtcNow;

        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        var devices = await EventuallyAsync(
            () => ListAsync(admin, "/devices"),
            listed => listed.Any(d => d.GetProperty("lastReceivedAt").ValueKind != JsonValueKind.Null));

        var lastReceived = devices.ToDictionary(d => d.GetProperty("serialNumber").GetString()!, d => d.GetProperty("lastReceivedAt"));
        lastReceived["TRK-007"].GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(before);
        lastReceived["GEN-001"].ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_republished_Reading_is_stored_once_and_the_first_copy_wins()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        var t = DateTimeOffset.UtcNow;
        var original = TelemetryPublisher.Payload(1, 0, t, more: ""","fuelPct":50""");
        await using var device = await TelemetryPublisher.ConnectAsync(Fixture);

        await device.PublishAsync("TRK-007", original);
        await HistoryWhenAsync(admin, asset, count: 1);
        // The same message again, then a conflicting copy of the same Reading, in later batches...
        await device.PublishAsync("TRK-007", original);
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, t.AddSeconds(5), more: ""","fuelPct":10"""));
        // ...and both copies of one Reading inside one batch.
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, t.AddSeconds(1), more: ""","fuelPct":49"""));
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, t.AddSeconds(1), more: ""","fuelPct":9"""));
        // A different Boot restarts the Sequence number, so this is a new Reading (ADR 0001).
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(2, 0, t.AddSeconds(2), more: ""","fuelPct":48"""));
        var history = await HistoryWhenAsync(admin, asset, count: 3);

        history.Select(reading => (reading.GetProperty("boot").GetInt32(), reading.GetProperty("sequenceNumber").GetInt64(), reading.GetProperty("fuelPct").GetSingle()))
            .ShouldBe([(1, 0L, 50f), (1, 1L, 49f), (2, 0L, 48f)]);
    }

    [Fact]
    public async Task Readings_are_stamped_with_the_Asset_whose_Attachment_covers_their_Device_time_across_a_Device_swap()
    {
        var time = new AdjustableTimeProvider();
        await using var api = await StartIngestAsync(time);
        using var admin = await AdminClientAsync(api);
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var truck = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var spare = await CreateAsync(admin, "/assets", new { name = "Haul truck 8", type = "Truck" });
        // The Device was on truck 7 from 3 h ago until 1 h ago, and has been on truck 8 since.
        time.Offset = TimeSpan.FromHours(-3);
        var first = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = truck });
        time.Offset = TimeSpan.FromHours(-1);
        await admin.PostAsync($"/attachments/{first}/detach", null, CancellationToken);
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = spare });
        time.Offset = TimeSpan.Zero;
        var now = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        // Published after the swap, in an order unrelated to Device time, as a backlog would arrive.
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 3, now.AddMinutes(-1)));
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, now.AddHours(-2)));
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, now.AddHours(-4)));
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 2, now.AddMinutes(-30)));
        var spareHistory = await HistoryWhenAsync(admin, spare, count: 2, from: now.AddDays(-1));
        var truckHistory = await HistoryWhenAsync(admin, truck, count: 1, from: now.AddDays(-1));

        spareHistory.Select(reading => reading.GetProperty("sequenceNumber").GetInt64()).ShouldBe([2L, 3L]);
        truckHistory.Select(reading => reading.GetProperty("sequenceNumber").GetInt64()).ShouldBe([1L]);
    }

    [Fact]
    public async Task A_Reading_from_a_Device_with_no_Attachment_is_kept_without_an_Asset()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        await EventuallyAsync(
            () => ListAsync(admin, "/devices"),
            listed => listed.Single().GetProperty("lastReceivedAt").ValueKind != JsonValueKind.Null);
        using var deleted = await admin.DeleteAsync($"/devices/{device}", CancellationToken);

        // Kept: a Device with Readings is Archived, not removed (#25).
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/devices?includeArchived=true")).Single().GetProperty("archived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Messages_that_cannot_become_Readings_do_not_hold_up_the_ones_that_can()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        var now = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", "not json");
        await publisher.PublishAsync("TRK-007", """{"boot":1,"seq":0}""");
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, now, lat: 91));
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 2, now.AddHours(1)));
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 3, now.AddDays(-8)));
        await publisher.PublishAsync("UNKNOWN-1", TelemetryPublisher.Payload(1, 4, now));
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 5, now));
        var history = await HistoryWhenAsync(admin, asset, count: 1, from: now.AddDays(-9), to: now.AddDays(1));

        history.Single().GetProperty("sequenceNumber").GetInt64().ShouldBe(5L);
    }

    [Theory]
    [InlineData("", "2026-11-17T00:00:00Z")]
    [InlineData("2026-11-17T00:00:00Z", "2026-11-16T00:00:00Z")]
    [InlineData("2026-11-01T00:00:00Z", "2026-12-02T00:00:01Z")]
    public async Task Asset_history_refuses_a_missing_backwards_or_too_long_range(string from, string to)
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });

        using var response = await admin.GetAsync($"/assets/{asset}/readings?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Asset_history_of_an_unknown_Asset_is_not_found()
    {
        using var admin = await AdminClientAsync();

        using var response = await admin.GetAsync($"/assets/{Guid.NewGuid()}/readings?from=2026-11-16T00:00:00Z&to=2026-11-17T00:00:00Z", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Operators_read_Asset_history()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, "operator@fieldtrack.test", "Operator-pass-1");

        // A range given with an offset is the same instant in UTC.
        using var response = await client.GetAsync($"/assets/{asset}/readings?from={Uri.EscapeDataString("2026-11-16T02:00:00+02:00")}&to=2026-11-17T00:00:00Z", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<(string Device, string Asset)> AttachedDeviceAsync(HttpClient admin, string serialNumber)
    {
        var device = await CreateAsync(admin, "/devices", new { serialNumber });
        var asset = await CreateAsync(admin, "/assets", new { name = $"Asset of {serialNumber}", type = "Truck" });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        return (device, asset);
    }

    private static async Task<JsonElement[]> HistoryWhenAsync(
        HttpClient admin, string asset, int count, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var range = $"from={Uri.EscapeDataString(Format(from ?? DateTimeOffset.UtcNow.AddHours(-1)))}&to={Uri.EscapeDataString(Format(to ?? DateTimeOffset.UtcNow.AddHours(1)))}";
        var history = await EventuallyAsync(() => ListAsync(admin, $"/assets/{asset}/readings?{range}"), readings => readings.Length >= count);
        history.Length.ShouldBe(count);
        return history;
    }

    private static string Format(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);
}
