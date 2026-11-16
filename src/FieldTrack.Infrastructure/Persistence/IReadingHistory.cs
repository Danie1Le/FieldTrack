namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// Answers whether an Asset or Device has Readings, which decides whether deleting it removes it or
/// Archives it (#24, #25). Implementations query through the request's <see cref="FieldTrackDbContext"/>,
/// so the answer is read inside the caller's transaction.
/// </summary>
public interface IReadingHistory
{
    /// <summary>True when at least one Reading is stamped with this Asset.</summary>
    Task<bool> AssetHasReadingsAsync(Guid assetId, CancellationToken cancellationToken);

    /// <summary>True when at least one Reading came from this Device.</summary>
    Task<bool> DeviceHasReadingsAsync(Guid deviceId, CancellationToken cancellationToken);
}
