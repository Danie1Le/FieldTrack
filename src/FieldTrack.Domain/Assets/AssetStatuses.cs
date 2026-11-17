namespace FieldTrack.Domain.Assets;

/// <summary>Computes <see cref="AssetStatus"/>, so the REST read and the live push agree on every Asset.</summary>
public static class AssetStatuses
{
    /// <summary>
    /// How long an Asset's Device may send nothing before the Asset shows Offline, whatever its Offline Alert Rules
    /// say (spec #21 story 26: global 120 s by Received time). Silence of exactly this long is Offline; the Offline
    /// Alert sweep (#33) uses the same comparison so the two never disagree at the boundary.
    /// </summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(120);

    /// <summary>The status of an Asset, by precedence No device, Alerting, Offline, Normal.</summary>
    /// <param name="attachedAt">When the Asset's open Attachment began; null when no Device is attached.</param>
    /// <param name="hasActiveAlert">Whether any of the Asset's Alerts is Active (Open or Acknowledged, Offline Alerts included).</param>
    /// <param name="lastReceivedAt">The attached Device's latest Received time; null before its first Reading.</param>
    /// <param name="now">The time to judge silence at.</param>
    public static AssetStatus Of(DateTimeOffset? attachedAt, bool hasActiveAlert, DateTimeOffset? lastReceivedAt, DateTimeOffset now) =>
        (attachedAt, hasActiveAlert) switch
        {
            (null, _) => AssetStatus.NoDevice,
            (_, true) => AssetStatus.Alerting,
            // Story 26: silence counts from the later of the last Reading and the attach, so a newly attached or
            // re-fitted Device gets its full 120 s before it shows Offline.
            ({ } attached, _) when now >= OfflineFrom(attached, lastReceivedAt) => AssetStatus.Offline,
            _ => AssetStatus.Normal,
        };

    /// <summary>
    /// When an Asset whose Device was attached at <paramref name="attachedAt"/> and last received a Reading at
    /// <paramref name="lastReceivedAt"/> (null if never) shows Offline, unless a Reading arrives first.
    /// </summary>
    public static DateTimeOffset OfflineFrom(DateTimeOffset attachedAt, DateTimeOffset? lastReceivedAt) =>
        SilentSince(attachedAt, lastReceivedAt) + OfflineAfter;

    /// <summary>
    /// When an attached Device's silence began: its last Received time, or its attach when that is later, so a newly
    /// attached or re-fitted Device gets its full time. Offline Alert Rules count from here too.
    /// </summary>
    public static DateTimeOffset SilentSince(DateTimeOffset attachedAt, DateTimeOffset? lastReceivedAt) =>
        lastReceivedAt > attachedAt ? lastReceivedAt.Value : attachedAt;
}
