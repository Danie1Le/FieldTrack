using System.ComponentModel.DataAnnotations;
using FieldTrack.Domain.Assets;

namespace FieldTrack.Api.Assets;

/// <summary>An Asset as an Admin creates or edits it.</summary>
/// <param name="Name">The name people know the Asset by.</param>
/// <param name="Type">The kind of equipment.</param>
/// <param name="AssetGroupId">The Asset Group it belongs to; null for none.</param>
public sealed record AssetRequest(
    [Required, MaxLength(200)] string Name,
    // Nullable so a missing type is refused instead of silently becoming the first enum value.
    [Required] AssetType? Type,
    Guid? AssetGroupId);

/// <summary>An Asset as anyone signed in sees it.</summary>
/// <param name="Id">The Asset's id.</param>
/// <param name="Name">The name people know the Asset by.</param>
/// <param name="Type">The kind of equipment.</param>
/// <param name="AssetGroupId">The Asset Group it belongs to, or null.</param>
/// <param name="Archived">True when the Asset was removed from use but kept for its history.</param>
public sealed record AssetResponse(Guid Id, string Name, AssetType Type, Guid? AssetGroupId, bool Archived);

/// <summary>An Asset Group as an Admin creates or renames it.</summary>
/// <param name="Name">The name people know the group by, such as a site or fleet.</param>
public sealed record AssetGroupRequest([Required, MaxLength(200)] string Name);

/// <summary>An Asset Group as anyone signed in sees it.</summary>
/// <param name="Id">The group's id.</param>
/// <param name="Name">The group's name.</param>
public sealed record AssetGroupResponse(Guid Id, string Name);
