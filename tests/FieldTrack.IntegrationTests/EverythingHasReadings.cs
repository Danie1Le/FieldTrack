using FieldTrack.Infrastructure.Persistence;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Answers that every Asset and Device has Readings, so tests reach the Archive branch of delete
/// before #26 stores real Readings.
/// </summary>
internal sealed class EverythingHasReadings : IReadingHistory
{
    public Task<bool> AssetHasReadingsAsync(Guid assetId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> DeviceHasReadingsAsync(Guid deviceId, CancellationToken cancellationToken) => Task.FromResult(true);
}
