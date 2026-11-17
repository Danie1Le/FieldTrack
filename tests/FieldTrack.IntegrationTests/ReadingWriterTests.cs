using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Ingest;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// <see cref="ReadingWriter"/> on its own, so a batch holds exactly the messages a test chooses,
/// which MQTT timing cannot promise.
/// </summary>
public sealed class ReadingWriterTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Within_one_batch_the_first_copy_of_a_Reading_wins()
    {
        using var admin = await AdminClientAsync();
        var device = Guid.Parse(await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" }));
        var now = DateTimeOffset.UtcNow;

        await WriteAsync(
            new ReceivedReading(TelemetryPublisher.TopicOf("TRK-007"), "TRK-007", new Reading(1, 1, now, 51.5, -0.12, null, null, 49f, null, null), now, [1], false),
            new ReceivedReading(TelemetryPublisher.TopicOf("TRK-007"), "TRK-007", new Reading(1, 1, now, 51.5, -0.12, null, null, 9f, null, null), now, [2], false));

        await using var database = await OpenDatabaseAsync();
        (await SqlAsync(database, "SELECT count(*) FROM readings WHERE device_id = @p0", device)).ShouldBe(1L);
        (await SqlAsync(database, "SELECT fuel_pct FROM readings WHERE device_id = @p0", device)).ShouldBe(49f);
    }

    [Fact]
    public async Task Each_dropped_copy_is_identical_or_conflicting_against_the_stored_copy_not_against_other_copies()
    {
        using var admin = await AdminClientAsync();
        await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var now = DateTimeOffset.UtcNow;
        await using var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, CancellationToken);
        var first = await writer.WriteAsync([Received(0, now) with { Payload = [1] }], [], CancellationToken);

        var later = await writer.WriteAsync(
        [
            // Copies of Reading 0, stored by the earlier batch with payload [1]: the two [2] copies match each other
            // but not the stored copy, so both conflict.
            Received(0, now) with { Payload = [1] },
            Received(0, now) with { Payload = [2] },
            Received(0, now) with { Payload = [2] },
            // Reading 1 is new: its first copy is stored and the others are judged against it.
            Received(1, now) with { Payload = [3] },
            Received(1, now) with { Payload = [3] },
            Received(1, now) with { Payload = [4] },
        ], [], CancellationToken);

        (first.IdenticalDuplicates, first.ConflictingDuplicates).ShouldBe((0, 0));
        (later.IdenticalDuplicates, later.ConflictingDuplicates).ShouldBe((2, 3));
        await using var database = await OpenDatabaseAsync();
        (await SqlAsync(database, "SELECT count(*) FROM readings")).ShouldBe(2L);
    }

    [Fact]
    public async Task A_redelivered_Rejected_message_already_stored_is_skipped_but_a_fresh_copy_is_stored()
    {
        var now = DateTimeOffset.UtcNow;
        var badJson = new ReceivedRejectedMessage(TelemetryPublisher.TopicOf("TRK-007"), [0x7b], RejectReason.BadJson, now, false);
        var unknown = new ReceivedReading(
            TelemetryPublisher.TopicOf("GHOST-1"), "GHOST-1", new Reading(1, 0, now, 51.5, -0.12, null, null, null, null, null), now, [0x7d], false);
        await using var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, CancellationToken);

        (await writer.WriteAsync([unknown], [badJson], CancellationToken)).Rejected.Count.ShouldBe(2);
        // The broker's redelivery (DUP) of both, after the batch committed.
        (await writer.WriteAsync([unknown with { Redelivered = true }], [badJson with { Redelivered = true }], CancellationToken)).Rejected.ShouldBeEmpty();
        // Published again by the Device: a new message, so a new Rejected message.
        (await writer.WriteAsync([unknown], [badJson], CancellationToken)).Rejected.Count.ShouldBe(2);

        await using var database = await OpenDatabaseAsync();
        (await SqlAsync(database, "SELECT count(*) FROM rejected_messages")).ShouldBe(4L);
    }

    [Fact]
    public async Task Among_overlapping_Attachments_the_later_one_wins_and_each_ends_just_before_its_detach_time()
    {
        var time = new AdjustableTimeProvider();
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        using var admin = await AdminClientAsync(api);
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var truck = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var spare = await CreateAsync(admin, "/assets", new { name = "Haul truck 8", type = "Truck" });
        // Truck 7 from -3 h to -1 h; truck 8, attached later, from -2 h to -1.5 h, inside truck 7's period.
        time.Offset = TimeSpan.FromHours(-3);
        var first = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = truck });
        time.Offset = TimeSpan.FromHours(-1);
        (await admin.PostAsync($"/attachments/{first}/detach", null, CancellationToken)).EnsureSuccessStatusCode();
        time.Offset = TimeSpan.FromHours(-2);
        var second = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = spare });
        time.Offset = TimeSpan.FromHours(-1.5);
        (await admin.PostAsync($"/attachments/{second}/detach", null, CancellationToken)).EnsureSuccessStatusCode();
        // Read back as stored, so the boundary Readings fall exactly on them.
        var spareAttachment = (await ListAsync(admin, $"/attachments?deviceId={device}")).Single(a => a.GetProperty("id").GetString() == second);
        var spareStart = spareAttachment.GetProperty("attachedAt").GetDateTimeOffset();
        var spareEnd = spareAttachment.GetProperty("detachedAt").GetDateTimeOffset();

        await WriteAsync(Received(0, spareStart), Received(1, spareStart.AddMinutes(15)), Received(2, spareEnd));

        await using var database = await OpenDatabaseAsync();
        const string Stamp = "SELECT asset_id FROM readings WHERE device_id = @p0 AND seq = @p1";
        (await SqlAsync(database, Stamp, Guid.Parse(device), 0L)).ShouldBe(Guid.Parse(spare));
        (await SqlAsync(database, Stamp, Guid.Parse(device), 1L)).ShouldBe(Guid.Parse(spare));
        (await SqlAsync(database, Stamp, Guid.Parse(device), 2L)).ShouldBe(Guid.Parse(truck));
    }

    private static ReceivedReading Received(long seq, DateTimeOffset deviceTime) =>
        new(TelemetryPublisher.TopicOf("TRK-007"), "TRK-007", new Reading(1, seq, deviceTime, 51.5, -0.12, null, null, null, null, null), DateTimeOffset.UtcNow, [0], false);

    private async Task WriteAsync(params ReceivedReading[] readings)
    {
        await using var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, CancellationToken);
        await writer.WriteAsync(readings, [], CancellationToken);
    }
}
