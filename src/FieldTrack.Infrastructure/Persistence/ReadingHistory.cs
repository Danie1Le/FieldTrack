namespace FieldTrack.Infrastructure.Persistence;

/// <summary>The <see cref="IReadingHistory"/> the API uses.</summary>
// shortcut: no Readings table exists yet, so nothing has Readings; #26 replaces both answers with
// EXISTS (SELECT 1 FROM readings WHERE asset_id = @id) / (... WHERE device_id = @id) on the request's FieldTrackDbContext.
public sealed class ReadingHistory : IReadingHistory
{
    /// <inheritdoc />
    public Task<bool> AssetHasReadingsAsync(Guid assetId, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> DeviceHasReadingsAsync(Guid deviceId, CancellationToken cancellationToken) => Task.FromResult(false);
}
