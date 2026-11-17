namespace FieldTrack.Infrastructure.Ingest;

/// <summary>An Asset in use with a Device attached, as the 1 s sweep (ADR 0005) reads it to judge the Device's silence.</summary>
/// <param name="AssetId">The Asset.</param>
/// <param name="AssetGroupId">The Asset's Asset Group, if any, which picks the Alert Rules it meets.</param>
/// <param name="AttachedAt">When its open Attachment began.</param>
/// <param name="LastReceivedAt">The attached Device's latest Received time; null before its first Reading.</param>
public sealed record AttachedAsset(Guid AssetId, Guid? AssetGroupId, DateTimeOffset AttachedAt, DateTimeOffset? LastReceivedAt);
