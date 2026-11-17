using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FieldTrack.Api.Assets;
using FieldTrack.Api.Ingest;
using FieldTrack.Domain.Telemetry;

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
    public async Task A_republished_Reading_is_stored_once_the_first_copy_wins_and_each_copy_is_counted_as_identical_or_conflicting()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        using var identical = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.IdenticalDuplicatesInstrument);
        using var conflicting = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.ConflictingDuplicatesInstrument);
        var t = DateTimeOffset.UtcNow;
        var original = TelemetryPublisher.Payload(1, 0, t, more: ""","fuelPct":50""");
        var second = TelemetryPublisher.Payload(1, 1, t.AddSeconds(1), more: ""","fuelPct":49""");
        await using var device = await TelemetryPublisher.ConnectAsync(Fixture);

        await device.PublishAsync("TRK-007", original);
        await HistoryWhenAsync(admin, asset, count: 1);
        // The same message again, then a conflicting copy of the same Reading, in later batches...
        await device.PublishAsync("TRK-007", original);
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, t.AddSeconds(5), more: ""","fuelPct":10"""));
        // ...and copies of one Reading inside one batch: identical, then conflicting.
        await device.PublishAsync("TRK-007", second);
        await device.PublishAsync("TRK-007", second);
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, t.AddSeconds(1), more: ""","fuelPct":9"""));
        // A different Boot restarts the Sequence number, so this is a new Reading (ADR 0001).
        await device.PublishAsync("TRK-007", TelemetryPublisher.Payload(2, 0, t.AddSeconds(2), more: ""","fuelPct":48"""));
        var history = await HistoryWhenAsync(admin, asset, count: 3);

        history.Select(reading => (reading.GetProperty("boot").GetInt32(), reading.GetProperty("sequenceNumber").GetInt64(), reading.GetProperty("fuelPct").GetSingle()))
            .ShouldBe([(1, 0L, 50f), (1, 1L, 49f), (2, 0L, 48f)]);
        // Counted after commit, so the last batch's copies may land just after its Readings show up in history.
        (await EventuallyAsync(() => Task.FromResult(identical.Total), total => total >= 2)).ShouldBe(2L);
        (await EventuallyAsync(() => Task.FromResult(conflicting.Total), total => total >= 2)).ShouldBe(2L);
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
        await using var database = await OpenDatabaseAsync();
        (await SqlAsync(database, "SELECT count(*) FROM readings WHERE device_id = @p0 AND asset_id IS NULL", Guid.Parse(device))).ShouldBe(1L);
        (await SqlAsync(database, "SELECT count(*) FROM readings")).ShouldBe(1L);
    }

    [Fact]
    public async Task A_batch_that_fails_is_not_acknowledged_so_the_broker_redelivers_it_and_it_is_stored_once()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        var now = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var partition = string.Create(CultureInfo.InvariantCulture, $"readings_{day:yyyyMMdd}");
        await using var database = await OpenDatabaseAsync();
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        // Without its partition the Reading cannot be stored, so every batch holding it fails and rolls back.
        await SqlAsync(database, $"ALTER TABLE readings DETACH PARTITION {partition}");
        try
        {
            await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, now));
            // Long enough for the worker to fail, reconnect (2 s) and fail again on the redelivered message.
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken);
            (await SqlAsync(database, "SELECT count(*) FROM reading_keys")).ShouldBe(0L);
        }
        finally
        {
            await SqlAsync(database, string.Create(CultureInfo.InvariantCulture,
                $"ALTER TABLE readings ATTACH PARTITION {partition} FOR VALUES FROM ('{day:yyyy-MM-dd}T00:00:00Z') TO ('{day.AddDays(1):yyyy-MM-dd}T00:00:00Z')"));
        }

        // Acknowledging before commit would have lost it; instead the broker redelivers it on reconnect.
        await HistoryWhenAsync(admin, asset, count: 1);
    }

    [Fact]
    public async Task An_Asset_delete_that_comes_while_a_batch_is_storing_waits_for_it_and_then_Archives()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        await using var blocker = await OpenDatabaseAsync();
        await using var hold = await blocker.BeginTransactionAsync(CancellationToken);
        // Holds the batch in its last statement, after it has locked its Device and Asset.
        await SqlAsync(blocker, "LOCK TABLE reading_keys IN SHARE MODE");
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        await LockWaitsAsync(1);
        var delete = admin.DeleteAsync($"/assets/{asset}", CancellationToken);
        await LockWaitsAsync(2);
        await hold.CommitAsync(CancellationToken);
        using var deleted = await delete;

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/assets?includeArchived=true")).Single().GetProperty("archived").GetBoolean().ShouldBeTrue();
        await HistoryWhenAsync(admin, asset, count: 1);
    }

    [Fact]
    public async Task A_batch_that_comes_while_its_Device_is_being_removed_waits_and_stores_nothing_for_it()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var (device, _) = await AttachedDeviceAsync(admin, "TRK-007");
        var (_, other) = await AttachedDeviceAsync(admin, "TRK-008");
        await using var remover = await OpenDatabaseAsync();
        await using var removal = await remover.BeginTransactionAsync(CancellationToken);
        // What Device delete does for a Device without Readings (#25).
        await SqlAsync(remover, "SELECT FROM devices WHERE id = @p0 FOR UPDATE", Guid.Parse(device));
        await SqlAsync(remover, "DELETE FROM attachments WHERE device_id = @p0", Guid.Parse(device));
        await SqlAsync(remover, "DELETE FROM devices WHERE id = @p0", Guid.Parse(device));
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        await LockWaitsAsync(1);
        await removal.CommitAsync(CancellationToken);
        await publisher.PublishAsync("TRK-008", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));

        // Ingest carries on, and nothing refers to the removed Device.
        await HistoryWhenAsync(admin, other, count: 1);
        (await SqlAsync(remover, "SELECT count(*) FROM readings WHERE device_id = @p0", Guid.Parse(device))).ShouldBe(0L);
        (await SqlAsync(remover, "SELECT count(*) FROM reading_keys WHERE device_id = @p0", Guid.Parse(device))).ShouldBe(0L);
    }

    [Fact]
    public async Task Asset_history_refuses_a_range_holding_more_than_MaxReadings()
    {
        using var admin = await AdminClientAsync();
        var (device, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        var from = DateTimeOffset.UtcNow.AddHours(-1);
        await using var database = await OpenDatabaseAsync();
        // One Reading per millisecond from `from`, one more than the cap.
        await SqlAsync(database, """
            INSERT INTO readings (device_id, boot, seq, asset_id, device_time, received_time, location)
            SELECT @p0, 1, g, @p1, @p2 + g * interval '1 millisecond', now(), ST_SetSRID(ST_MakePoint(0, 0), 4326)::geography
            FROM generate_series(0, @p3) g
            """, Guid.Parse(device), Guid.Parse(asset), from, (long)ReadingsController.MaxReadings);

        using var tooMany = await admin.GetAsync($"/assets/{asset}/readings?{Range(from, from.AddHours(1))}", CancellationToken);
        using var atTheCap = await admin.GetAsync($"/assets/{asset}/readings?{Range(from, from.AddMilliseconds(ReadingsController.MaxReadings))}", CancellationToken);

        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        atTheCap.StatusCode.ShouldBe(HttpStatusCode.OK);
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
        // Stored plus rejected equals published (spec #21); the rejects were in this batch or an earlier one.
        (await ListAsync(admin, "/rejected-messages")).Length.ShouldBe(6);
    }

    [Theory]
    [InlineData(RejectReason.BadJson)]
    [InlineData(RejectReason.MissingField)]
    [InlineData(RejectReason.PositionOutOfRange)]
    [InlineData(RejectReason.TooFarAhead)]
    [InlineData(RejectReason.TooLate)]
    [InlineData(RejectReason.UnknownTopicVersion)]
    [InlineData(RejectReason.UnknownSerialNumber)]
    [InlineData(RejectReason.ArchivedDevice)]
    public async Task A_message_that_cannot_become_a_Reading_is_stored_as_a_Rejected_message_and_counted(RejectReason reason)
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        await AttachedDeviceAsync(admin, "TRK-007");
        await AttachedDeviceAsync(admin, "OLD-001");
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, "UPDATE devices SET archived = true WHERE serial_number = 'OLD-001'");
        using var rejects = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.RejectedMessagesInstrument);
        var now = DateTimeOffset.UtcNow;
        var (topic, payload) = reason switch
        {
            RejectReason.BadJson => (TelemetryPublisher.TopicOf("TRK-007"), "{\"boot\":1,"),
            RejectReason.MissingField => (TelemetryPublisher.TopicOf("TRK-007"), """{"boot":1,"seq":0,"ts":0}"""),
            RejectReason.PositionOutOfRange => (TelemetryPublisher.TopicOf("TRK-007"), TelemetryPublisher.Payload(1, 0, now, lon: 180.5)),
            RejectReason.TooFarAhead => (TelemetryPublisher.TopicOf("TRK-007"), TelemetryPublisher.Payload(1, 0, now.AddMinutes(6))),
            RejectReason.TooLate => (TelemetryPublisher.TopicOf("TRK-007"), TelemetryPublisher.Payload(1, 0, now.AddDays(-8))),
            RejectReason.UnknownTopicVersion => ("fieldtrack/v2/devices/TRK-007/telemetry", TelemetryPublisher.Payload(1, 0, now)),
            RejectReason.UnknownSerialNumber => (TelemetryPublisher.TopicOf("GHOST-1"), TelemetryPublisher.Payload(1, 0, now)),
            RejectReason.ArchivedDevice => (TelemetryPublisher.TopicOf("OLD-001"), TelemetryPublisher.Payload(1, 0, now)),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishToAsync(topic, payload);
        var rejected = (await EventuallyAsync(() => ListAsync(admin, "/rejected-messages"), listed => listed.Length > 0)).ShouldHaveSingleItem();

        rejected.GetProperty("reason").GetString().ShouldBe(reason.ToString());
        rejected.GetProperty("topic").GetString().ShouldBe(topic);
        rejected.GetProperty("payload").GetBytesFromBase64().ShouldBe(Encoding.UTF8.GetBytes(payload));
        rejected.GetProperty("receivedTime").GetDateTimeOffset().ShouldBeInRange(now, DateTimeOffset.UtcNow);
        (await SqlAsync(database, "SELECT count(*) FROM readings")).ShouldBe(0L);
        var counted = await EventuallyAsync(() => Task.FromResult(rejects.Measurements), measured => measured.Count > 0);
        counted.ShouldHaveSingleItem().Value.ShouldBe(1L);
        counted[0].Tags[IngestMetrics.ReasonTag].ShouldBe(reason.ToString());
    }

    [Fact]
    public async Task A_topic_longer_than_MaxTopicLength_is_stored_clipped_and_marked()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var topic = TelemetryPublisher.TopicOf(new string('X', 300));
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        await publisher.PublishToAsync(topic, TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        var rejected = (await EventuallyAsync(() => ListAsync(admin, "/rejected-messages"), listed => listed.Length > 0)).ShouldHaveSingleItem();

        rejected.GetProperty("reason").GetString().ShouldBe(nameof(RejectReason.UnknownSerialNumber));
        rejected.GetProperty("topic").GetString().ShouldBe(topic[..RejectedMessage.MaxTopicLength]);
        rejected.GetProperty("topicClipped").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_Rejected_message_redelivered_after_its_batch_committed_is_stored_and_counted_once()
    {
        var clientId = NewIngestClientId();
        await using var api = await StartIngestAsync(clientId: clientId);
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        using var rejects = new InstrumentRecorder(api.Services, IngestMetrics.MeterName, IngestMetrics.RejectedMessagesInstrument);
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        await using var blocker = await OpenDatabaseAsync();

        await using (var hold = await blocker.BeginTransactionAsync(CancellationToken))
        {
            // Holds the batch in its last statement, before it commits.
            await SqlAsync(blocker, "LOCK TABLE rejected_messages IN SHARE MODE");
            await publisher.PublishAsync("GHOST-1", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
            await LockWaitsAsync(1);
            // The worker loses its broker connection, so the PUBACKs after this commit are lost, as in a crash.
            await TelemetryPublisher.TakeOverIngestSessionAsync(Fixture, clientId);
            await hold.CommitAsync(CancellationToken);
        }

        // The worker reconnects and the broker redelivers the message, marked DUP, ahead of this one.
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        await HistoryWhenAsync(admin, asset, count: 1);

        await using var database = await OpenDatabaseAsync();
        (await SqlAsync(database, "SELECT count(*) FROM rejected_messages")).ShouldBe(1L);
        rejects.Measurements.Sum(measured => measured.Value).ShouldBe(1L);
    }

    [Fact]
    public async Task A_session_still_subscribed_to_the_retired_v1_filter_receives_each_message_once()
    {
        var clientId = NewIngestClientId();
        // The filter ingest subscribed to before Rejected messages, kept by a session that outlived the upgrade.
        await TelemetryPublisher.CreateIngestSessionAsync(Fixture, clientId, "fieldtrack/v1/devices/+/telemetry");
        await using var api = await StartIngestAsync(clientId: clientId);
        using var admin = await AdminClientAsync(api);
        var (_, asset) = await AttachedDeviceAsync(admin, "TRK-007");
        var now = DateTimeOffset.UtcNow;
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);
        // Stored once the worker has connected, and so has unsubscribed.
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, now));
        await HistoryWhenAsync(admin, asset, count: 1);

        await publisher.PublishAsync("TRK-007", "not json");
        await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 1, now));
        await HistoryWhenAsync(admin, asset, count: 2);

        (await ListAsync(admin, "/rejected-messages")).ShouldHaveSingleItem();
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
        var range = Range(from ?? DateTimeOffset.UtcNow.AddHours(-1), to ?? DateTimeOffset.UtcNow.AddHours(1));
        var history = await EventuallyAsync(() => ListAsync(admin, $"/assets/{asset}/readings?{range}"), readings => readings.Length >= count);
        history.Length.ShouldBe(count);
        return history;
    }

    private static string Range(DateTimeOffset from, DateTimeOffset to) =>
        $"from={Uri.EscapeDataString(Format(from))}&to={Uri.EscapeDataString(Format(to))}";

    /// <summary>Waits until <paramref name="count"/> sessions on the database are waiting for a lock.</summary>
    private async Task LockWaitsAsync(int count)
    {
        await using var database = await OpenDatabaseAsync();
        var waiting = await EventuallyAsync(
            async () => (long)(await SqlAsync(database, "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()"))!,
            waiting => waiting >= count);
        waiting.ShouldBe(count);
    }

    private static string Format(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);
}
