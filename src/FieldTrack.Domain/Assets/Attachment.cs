namespace FieldTrack.Domain.Assets;

/// <summary>
/// The period during which a Device was fitted to an Asset. Kept after it ends, so ingest can stamp
/// each Reading with the Asset whose Attachment covers its Device time even after the Device moves.
/// </summary>
public sealed class Attachment
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The Device that was fitted.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>The Asset it was fitted to.</summary>
    public Guid AssetId { get; set; }

    /// <summary>When the Device was attached.</summary>
    public DateTimeOffset AttachedAt { get; set; }

    /// <summary>When the Device was detached, or null while the Attachment is open.</summary>
    public DateTimeOffset? DetachedAt { get; set; }
}
