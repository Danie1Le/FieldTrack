namespace FieldTrack.Domain.Assets;

/// <summary>
/// The kind of equipment an Asset is. Fixed by the spec (#21) so the dashboard can draw a kind
/// letter inside each map marker.
/// </summary>
public enum AssetType
{
    /// <summary>A haul or service truck.</summary>
    Truck,

    /// <summary>A tracked or wheeled excavator working a site.</summary>
    Excavator,

    /// <summary>A mobile or standby generator powering a site.</summary>
    Generator,

    /// <summary>
    /// A fixed monitored installation, such as a substation sensor; an Asset, not the Device that
    /// reports for it.
    /// </summary>
    Sensor,
}
