namespace FieldTrack.Domain.Assets;

/// <summary>
/// The kind of equipment an Asset is. Fixed by the spec (#21) so the dashboard can draw a kind
/// letter inside each map marker.
/// </summary>
public enum AssetType
{
    /// <summary>A haul or service truck.</summary>
    Truck,

    /// <summary>An excavator.</summary>
    Excavator,

    /// <summary>A generator.</summary>
    Generator,

    /// <summary>A fixed sensor, such as one at a substation.</summary>
    Sensor,
}
