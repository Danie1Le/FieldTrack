namespace FieldTrack.Infrastructure.Persistence;

/// <summary>The <see cref="IReadingHistory"/> the API uses.</summary>
public sealed class ReadingHistory : IReadingHistory
{
    /// <inheritdoc />
    // shortcut: no Readings table exists yet, so no Asset has Readings; #26 replaces this with
    // EXISTS (SELECT 1 FROM readings WHERE asset_id = @assetId) on the request's FieldTrackDbContext.
    public Task<bool> AssetHasReadingsAsync(Guid assetId, CancellationToken cancellationToken) => Task.FromResult(false);
}
