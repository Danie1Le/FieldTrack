using System.ComponentModel.DataAnnotations;

namespace FieldTrack.Api.Assets;

/// <summary>A request to export one Asset's Readings as CSV.</summary>
/// <param name="From">Start of the Device-time range, inclusive.</param>
/// <param name="To">End of the Device-time range, exclusive; at most 31 days after <paramref name="From"/>.</param>
public sealed record ExportRequest([Required] DateTimeOffset? From, [Required] DateTimeOffset? To);

/// <summary>Where to download an export, for the browser to open (no access token needed).</summary>
/// <param name="Url">The download path, relative to the API root; it works once.</param>
/// <param name="ExpiresAt">After this the URL no longer works.</param>
public sealed record ExportResponse(string Url, DateTimeOffset ExpiresAt);
