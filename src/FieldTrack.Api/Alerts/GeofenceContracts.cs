using System.ComponentModel.DataAnnotations;
using FieldTrack.Infrastructure.Persistence;

namespace FieldTrack.Api.Alerts;

/// <summary>A Geofence as an Admin creates or edits it.</summary>
/// <param name="Name">The name people know the Geofence by; Geofence Alerts quote it.</param>
/// <param name="Kind"><c>Inclusion</c> or <c>Exclusion</c>: what the area is for, which says how the map draws it.</param>
/// <param name="Area">The area, one GeoJSON polygon in WGS 84.</param>
public sealed record GeofenceRequest(
    [Required, MaxLength(200)] string Name,
    // Nullable so a missing value is refused instead of silently becoming the first enum value.
    [Required] GeofenceKind? Kind,
    [Required] GeoJsonPolygon? Area);

/// <summary>A Geofence as anyone signed in sees it.</summary>
/// <param name="Id">The Geofence's id.</param>
/// <param name="Name">The name people know the Geofence by.</param>
/// <param name="Kind">What the area is for.</param>
/// <param name="Area">The area, one GeoJSON polygon in WGS 84.</param>
public sealed record GeofenceResponse(Guid Id, string Name, GeofenceKind Kind, GeoJsonPolygon Area);

/// <summary>
/// A GeoJSON Polygon geometry (RFC 7946 3.1.6), the shape map drawing tools produce: an outer ring and optional holes,
/// each a closed list of <c>[longitude, latitude]</c> positions.
/// </summary>
/// <param name="Type">Always <c>Polygon</c>.</param>
/// <param name="Coordinates">The rings, outer ring first.</param>
public sealed record GeoJsonPolygon([Required] string? Type, [Required] double[][][]? Coordinates);
