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
            new ReceivedReading(TelemetryPublisher.TopicOf("TRK-007"), "TRK-007", new Reading(1, 1, now, 51.5, -0.12, null, null, 49f, null, null), now, [1]),
            new ReceivedReading(TelemetryPublisher.TopicOf("TRK-007"), "TRK-007", new Reading(1, 1, now, 51.5, -0.12, null, null, 9f, null, null), now, [2]));

        await using var database = await OpenDatabaseAsync();
        (await SqlAsync(database, "SELECT count(*) FROM readings WHERE device_id = @p0", device)).ShouldBe(1L);
        (await SqlAsync(database, "SELECT fuel_pct FROM readings WHERE device_id = @p0", device)).ShouldBe(49f);
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
        new(TelemetryPublisher.TopicOf("TRK-007"), "TRK-007", new Reading(1, seq, deviceTime, 51.5, -0.12, null, null, null, null, null), DateTimeOffset.UtcNow, [0]);

    private async Task WriteAsync(params ReceivedReading[] readings)
    {
        await using var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken);
        await writer.WriteAsync(readings, [], CancellationToken);
    }
}
