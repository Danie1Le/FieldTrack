using System.ComponentModel.DataAnnotations;

namespace FieldTrack.Api.Assets;

/// <summary>A Device as an Admin registers it.</summary>
/// <param name="SerialNumber">
/// The serial number the Device publishes under. It becomes one level of the telemetry topic
/// <c>fieldtrack/v1/devices/{serialNumber}/telemetry</c>, so it is limited to letters, digits, '.', '_' and '-'
/// (no '/', '+', '#' or spaces).
/// </param>
public sealed record DeviceRequest([Required, MaxLength(64), RegularExpression("^[A-Za-z0-9._-]+$")] string SerialNumber);

/// <summary>A Device as anyone signed in sees it.</summary>
/// <param name="Id">The Device's id.</param>
/// <param name="SerialNumber">The serial number it publishes under.</param>
/// <param name="Archived">True when the Device was removed from use but kept for its history.</param>
/// <param name="LastReceivedAt">The Received time of its latest stored Readings, or null before any.</param>
public sealed record DeviceResponse(Guid Id, string SerialNumber, bool Archived, DateTimeOffset? LastReceivedAt);

/// <summary>An Admin's request to attach a Device to an Asset, starting now.</summary>
/// <param name="DeviceId">The Device to attach.</param>
/// <param name="AssetId">The Asset to attach it to.</param>
public sealed record AttachmentRequest([Required] Guid? DeviceId, [Required] Guid? AssetId);

/// <summary>An Attachment as anyone signed in sees it.</summary>
/// <param name="Id">The Attachment's id.</param>
/// <param name="DeviceId">The Device that was fitted.</param>
/// <param name="AssetId">The Asset it was fitted to.</param>
/// <param name="AttachedAt">When the Device was attached.</param>
/// <param name="DetachedAt">When it was detached, or null while still attached.</param>
public sealed record AttachmentResponse(Guid Id, Guid DeviceId, Guid AssetId, DateTimeOffset AttachedAt, DateTimeOffset? DetachedAt);
