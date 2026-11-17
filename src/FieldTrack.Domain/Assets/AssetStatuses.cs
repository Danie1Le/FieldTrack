namespace FieldTrack.Domain.Assets;

/// <summary>Computes <see cref="AssetStatus"/>, so the REST read and the live push agree on every Asset.</summary>
public static class AssetStatuses
{
    /// <summary>
    /// How long an Asset's Device may send nothing before the Asset shows Offline, whatever its Offline Alert Rules
    /// say (spec #21: global 120 s by Received time).
    /// </summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The status of an Asset, by precedence No device, Alerting, Offline, Normal. <paramref name="hasDevice"/> says
    /// whether a Device is attached now, <paramref name="hasActiveAlert"/> whether any of the Asset's Alerts is Active
    /// (an Offline Alert counts), and <paramref name="lastReceivedAt"/> is that Device's latest Received time, null
    /// before its first Reading.
    /// </summary>
    public static AssetStatus Of(bool hasDevice, bool hasActiveAlert, DateTimeOffset? lastReceivedAt, DateTimeOffset now) =>
        !hasDevice ? AssetStatus.NoDevice
        : hasActiveAlert ? AssetStatus.Alerting
        // A Device that never reported counts as silent, so a newly attached one that stays quiet stands out.
        : lastReceivedAt is not { } received || now - received > OfflineAfter ? AssetStatus.Offline
        : AssetStatus.Normal;
}
