using FieldTrack.Infrastructure.Ingest;
using Npgsql;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Crash safety (ADR 0004, ADR 0005): a worker throws at a crash point, is disposed, and a new one starts on the same
/// broker session; every published Reading is then stored exactly once, with exactly one Alert and the right rule state.
/// </summary>
public sealed class IngestCrashTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData(IngestCrashPoint.AfterStage)]
    [InlineData(IngestCrashPoint.AfterEvaluation)]
    [InlineData(IngestCrashPoint.AfterCommit)]
    public async Task A_worker_replaced_after_a_crash_at_a_crash_point_stores_each_Reading_once_and_raises_one_Alert(IngestCrashPoint point)
    {
        var clientId = NewIngestClientId();
        var crash = new CrashAt(point);
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var t = DateTimeOffset.UtcNow.AddSeconds(1);
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        // The session holds all three messages before the crashing worker connects, so they arrive as one batch holding the breach.
        await TelemetryPublisher.CreateIngestSessionAsync(Fixture, clientId);
        await publisher.PublishAsync("TRK-007", Temp(0, t, 90));
        await publisher.PublishAsync("TRK-007", Temp(1, t.AddSeconds(1), 110));
        await publisher.PublishAsync("TRK-007", Temp(2, t.AddSeconds(2), 112));
        await using (await StartIngestAsync(clientId: clientId, crashes: crash))
        {
            await crash.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken);
            await using var afterCrash = await OpenDatabaseAsync();
            var committed = point == IngestCrashPoint.AfterCommit;
            (await SqlAsync(afterCrash, "SELECT coalesce(array_agg(seq ORDER BY seq), '{}'::bigint[]) FROM readings"))
                .ShouldBe(committed ? new long[] { 0, 1, 2 } : Array.Empty<long>());
            (await ListAsync(admin, "/alerts")).Length.ShouldBe(committed ? 1 : 0);
        }

        await using var replacement = await StartIngestAsync(clientId: clientId);
        await publisher.PublishAsync("TRK-007", Temp(3, t.AddSeconds(3), 111));
        await using var database = await StoredWhenAsync(sequenceNumber: 3);

        (await SqlAsync(database, "SELECT array_agg(seq ORDER BY seq) FROM readings")).ShouldBe(new long[] { 0, 1, 2, 3 });
        var alert = (await ListAsync(admin, "/alerts")).ShouldHaveSingleItem();
        alert.GetProperty("status").GetString().ShouldBe("Open");
        alert.GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(1);
        (await SqlAsync(database, "SELECT count(*) FROM alert_rule_states WHERE breached AND last_device_id = @p0 AND last_sequence_number = 3",
            Guid.Parse(device))).ShouldBe(1L);
    }

    [Fact]
    public async Task A_breach_redelivered_after_a_crash_and_a_Device_swap_is_not_evaluated_again()
    {
        var clientId = NewIngestClientId();
        var crash = new CrashAt(IngestCrashPoint.AfterCommit);
        using var admin = await AdminClientAsync();
        var first = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var second = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-008" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var attachment = await CreateAsync(admin, "/attachments", new { deviceId = first, assetId = asset });
        await CreateAsync(admin, "/alert-rules", Overheat(asset));
        await using var publisher = await TelemetryPublisher.ConnectAsync(Fixture);

        // The breach commits and raises its Alert, but the worker dies before acknowledging it.
        await using (await StartIngestAsync(clientId: clientId, crashes: crash))
        {
            await publisher.PublishAsync("TRK-007", Temp(0, DateTimeOffset.UtcNow.AddSeconds(1), 110));
            await crash.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken);
        }

        using (var detached = await admin.PostAsync($"/attachments/{attachment}/detach", content: null, CancellationToken))
        {
            detached.EnsureSuccessStatusCode();
        }

        await CreateAsync(admin, "/attachments", new { deviceId = second, assetId = asset });
        // Another session stores the new Device's clear while the breach waits in the crashed session.
        await using (await StartIngestAsync())
        {
            await publisher.PublishAsync("TRK-008", Temp(0, DateTimeOffset.UtcNow.AddSeconds(1), 90));
            await EventuallyAsync(() => ListAsync(admin, "/alerts"), alerts => alerts.Any(alert => alert.GetProperty("status").GetString() == "Resolved"));
        }

        // The crashed session gets the breach back (DUP), then its copy of the clear. Another Device's Reading always
        // counts as newer (ADR 0005), so only the store statement's duplicate filter keeps the breach from raising again.
        await using var replacement = await StartIngestAsync(clientId: clientId);
        await publisher.PublishAsync("TRK-008", Temp(1, DateTimeOffset.UtcNow.AddSeconds(1), 91));
        await using var database = await StoredWhenAsync(sequenceNumber: 1);

        (await SqlAsync(database, "SELECT count(*) FROM readings")).ShouldBe(3L);
        (await ListAsync(admin, "/alerts")).ShouldHaveSingleItem().GetProperty("status").GetString().ShouldBe("Resolved");
        (await SqlAsync(database, "SELECT count(*) FROM alert_rule_states WHERE NOT breached AND last_device_id = @p0 AND last_sequence_number = 1",
            Guid.Parse(second))).ShouldBe(1L);
    }

    /// <summary>
    /// Waits until the Reading with <paramref name="sequenceNumber"/>, published after the replacement worker started, is
    /// stored. The broker delivers it after every message the crashed worker left unacknowledged, so those are done too.
    /// </summary>
    private async Task<NpgsqlConnection> StoredWhenAsync(long sequenceNumber)
    {
        var database = await OpenDatabaseAsync();
        var stored = await EventuallyAsync(
            async () => (long)(await SqlAsync(database, "SELECT count(*) FROM readings WHERE seq = @p0", sequenceNumber))!,
            count => count > 0);
        stored.ShouldBe(1L);
        return database;
    }

    private static object Overheat(string assetId) => new
    {
        name = "Engine overheat",
        kind = "Threshold",
        assetId,
        severity = "Critical",
        enabled = true,
        sensor = "EngineTempC",
        @operator = "GreaterThan",
        threshold = 105,
        hysteresis = 5,
    };

    /// <summary>Fails every batch at <paramref name="point"/>, as a worker that crashes there each time.</summary>
    private sealed class CrashAt(IngestCrashPoint point) : IIngestCrashPoints
    {
        /// <summary>Completes the first time a batch reaches the point.</summary>
        public TaskCompletionSource Crashed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reached(IngestCrashPoint reached)
        {
            if (reached == point)
            {
                Crashed.TrySetResult();
                throw new InvalidOperationException($"Injected crash at {point}.");
            }
        }
    }
}
