namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// A single-use permission to download one Asset's Readings for one Device-time range as CSV (#42). The browser
/// downloads through its own UI, so the URL carries this ticket's token instead of the user's access token, and only
/// the token's SHA-256 hash is stored. Kept in the database, so redeeming works across restarts and replicas.
/// </summary>
public sealed class ExportTicket
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the token in the download URL.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The user who asked for the export; their running exports count against the per-user limit.</summary>
    public Guid UserId { get; set; }

    /// <summary>The Asset whose Readings are exported.</summary>
    public Guid AssetId { get; set; }

    /// <summary>Start of the Device-time range, inclusive.</summary>
    public DateTimeOffset RangeStart { get; set; }

    /// <summary>End of the Device-time range, exclusive.</summary>
    public DateTimeOffset RangeEnd { get; set; }

    /// <summary>After this the ticket no longer downloads.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the download starts; a used ticket never downloads again.</summary>
    public bool Used { get; set; }
}
