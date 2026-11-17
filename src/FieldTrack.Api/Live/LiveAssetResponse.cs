using FieldTrack.Api.Assets;
using FieldTrack.Domain.Assets;

namespace FieldTrack.Api.Live;

/// <summary>An Asset as the dashboard shows it live: its Asset Status, where it is and what its sensors last said.</summary>
/// <param name="Id">The Asset's id.</param>
/// <param name="Name">The name people know it by.</param>
/// <param name="Type">Its Asset Type.</param>
/// <param name="AssetGroupId">Its Asset Group, or null.</param>
/// <param name="Status">Its Asset Status, computed when read.</param>
/// <param name="LastReceivedAt">When the attached Device's latest Reading was received ("last seen"), or null.</param>
/// <param name="Latest">Its Reading with the latest Device time, from any Device it has had, or null before its first.</param>
public sealed record LiveAssetResponse(
    Guid Id,
    string Name,
    AssetType Type,
    Guid? AssetGroupId,
    AssetStatus Status,
    DateTimeOffset? LastReceivedAt,
    ReadingResponse? Latest);
