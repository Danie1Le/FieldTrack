namespace FieldTrack.Domain.Alerts;

/// <summary>
/// The Geofence a Geofence rule watches, as the rules engine needs it. Containment is answered by the ingest worker,
/// which checks it with NetTopologySuite prepared geometries cached per Geofence (ADR 0005), so Domain itself stays
/// free of packages and its evaluator can be tested with any shape.
/// </summary>
public interface IGeofence
{
    /// <summary>The name people know the Geofence by, used in the Alerts its rules raise.</summary>
    string Name { get; }

    /// <summary>Whether the position at <paramref name="latitude"/>, <paramref name="longitude"/> (WGS 84 degrees) is inside the Geofence.</summary>
    bool Contains(double latitude, double longitude);
}
