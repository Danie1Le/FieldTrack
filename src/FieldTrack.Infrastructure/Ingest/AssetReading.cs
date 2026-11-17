using FieldTrack.Domain.Alerts;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>A Reading a batch stored, with the Asset in use it was stamped with, which picks the Alert Rules it meets.</summary>
/// <param name="AssetId">The Asset.</param>
/// <param name="AssetGroupId">The Asset's Asset Group, if any.</param>
/// <param name="AttachedDeviceId">The Device attached to the Asset now, if any; only its Readings end an Offline rule's silence.</param>
/// <param name="Reading">The stored Reading.</param>
public sealed record AssetReading(Guid AssetId, Guid? AssetGroupId, Guid? AttachedDeviceId, StoredReading Reading);
