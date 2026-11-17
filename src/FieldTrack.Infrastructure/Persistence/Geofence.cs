using FieldTrack.Domain.Alerts;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// A named area on the map; it affects Assets only through an Alert Rule that names it (GLOSSARY). It lives here, not
/// in Domain, because its area is a NetTopologySuite polygon stored as a PostGIS geography and Domain takes no
/// packages; the rules engine sees it as an <see cref="IGeofence"/>.
/// </summary>
public sealed class Geofence : IGeofence
{
    private IPreparedGeometry? _prepared;

    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The name people know the Geofence by, such as "North depot".</summary>
    public required string Name { get; set; }

    /// <summary>What the area is for, which says how the map draws it; the rules that name it decide what raises.</summary>
    public GeofenceKind Kind { get; set; }

    /// <summary>The area, in WGS 84 longitude/latitude (SRID 4326).</summary>
    public required Polygon Area { get; set; }

    /// <summary>
    /// Whether the position is inside the area or on its edge. Checked in memory against a prepared geometry built
    /// once per area (ADR 0005), as ingest checks every Reading of every Geofence rule.
    /// </summary>
    /// <remarks>
    /// Planar on degrees, so edges run straight in longitude/latitude rather than along great circles: the gap is
    /// negligible at site scale. An area crossing the antimeridian is not supported.
    /// </remarks>
    public bool Contains(double latitude, double longitude)
    {
        if (!ReferenceEquals(_prepared?.Geometry, Area))
        {
            _prepared = PreparedGeometryFactory.Prepare(Area);
        }

        return _prepared.Covers(new Point(longitude, latitude));
    }
}
