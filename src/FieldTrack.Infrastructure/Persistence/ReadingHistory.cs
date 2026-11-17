using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Infrastructure.Persistence;

/// <summary>The <see cref="IReadingHistory"/> the API uses, read through the request's <see cref="FieldTrackDbContext"/>.</summary>
public sealed class ReadingHistory(FieldTrackDbContext db) : IReadingHistory
{
    /// <inheritdoc />
    public Task<bool> AssetHasReadingsAsync(Guid assetId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM readings WHERE asset_id = {assetId}) AS \"Value\"").SingleAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeviceHasReadingsAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM readings WHERE device_id = {deviceId}) AS \"Value\"").SingleAsync(cancellationToken);
}
