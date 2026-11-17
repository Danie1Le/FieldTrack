using System.ComponentModel.DataAnnotations;

namespace FieldTrack.Api.Assets;

/// <summary>An Admin's request to attach a Device to an Asset, starting now.</summary>
/// <param name="DeviceId">The Device to attach.</param>
/// <param name="AssetId">The Asset to attach it to.</param>
public sealed record AttachmentRequest(
    // Nullable so a missing id is refused as required instead of binding as Guid.Empty ("No Device has this id").
    [Required] Guid? DeviceId,
    [Required] Guid? AssetId);

/// <summary>An Attachment as anyone signed in sees it.</summary>
/// <param name="Id">The Attachment's id.</param>
/// <param name="DeviceId">The Device that was fitted.</param>
/// <param name="AssetId">The Asset it was fitted to.</param>
/// <param name="AttachedAt">When the Device was attached.</param>
/// <param name="DetachedAt">When it was detached, or null while still attached.</param>
public sealed record AttachmentResponse(Guid Id, Guid DeviceId, Guid AssetId, DateTimeOffset AttachedAt, DateTimeOffset? DetachedAt);
