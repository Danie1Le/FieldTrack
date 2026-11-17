using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Base for Alert tests: an Engine overheat rule on an Asset fitted with a Device, and Readings written straight
/// through <see cref="ReadingWriter"/> so each test controls its batches exactly.
/// </summary>
public abstract class AlertTest(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    // A fresh writer per call, as a new ingest session would open.
    protected async Task WriteAsync(IReadOnlyList<ReceivedReading> readings)
    {
        await using var writer = await ReadingWriter.OpenAsync(Fixture.Api.Services.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, CancellationToken);
        await writer.WriteAsync(readings, [], CancellationToken);
    }

    protected static ReceivedReading Received(long seq, DateTimeOffset deviceTime, float engineTempC, string serialNumber = "TRK-007") =>
        new(TelemetryPublisher.TopicOf(serialNumber), serialNumber, new Reading(1, seq, Millis(deviceTime), 51.5, -0.12, null, engineTempC, null, null, null),
            DateTimeOffset.UtcNow, [(byte)seq], Redelivered: false);

    protected static DateTimeOffset Millis(DateTimeOffset time) => DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

#pragma warning disable IDE1006 // JSON property names
    protected sealed record RuleRequest(
        string name, string kind, string? assetId, string? assetGroupId, string severity, bool enabled,
        string sensor, string @operator, double threshold, double hysteresis);
#pragma warning restore IDE1006

    protected static RuleRequest Overheat(string? assetId = null, string? assetGroupId = null) =>
        new("Engine overheat", "Threshold", assetId, assetGroupId, "Critical", true, "EngineTempC", "GreaterThan", 105, 5);

    protected static async Task<(string Device, string Asset)> AttachedDeviceAsync(HttpClient admin, string serialNumber = "TRK-007", string? assetGroupId = null)
    {
        var device = await CreateAsync(admin, "/devices", new { serialNumber });
        var asset = await CreateAsync(admin, "/assets", new { name = $"Asset of {serialNumber}", type = "Truck", assetGroupId });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        return (device, asset);
    }
}
