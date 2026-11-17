using FieldTrack.Infrastructure.Persistence;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Answers that every Asset and Device has Readings, so tests of delete and attach rules reach the
/// Archive branch without publishing telemetry.
/// </summary>
internal sealed class EverythingHasReadings : IReadingHistory
{
    public Task<bool> AssetHasReadingsAsync(Guid assetId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> DeviceHasReadingsAsync(Guid deviceId, CancellationToken cancellationToken) => Task.FromResult(true);
}
