namespace FieldTrack.Domain.Assets;

/// <summary>
/// The live condition of an Asset, worked out on read from its Device and Active Alerts and never set by hand
/// (spec #21). <see cref="AssetStatuses.Of"/> applies the precedence: the members are in that order.
/// </summary>
public enum AssetStatus
{
    /// <summary>No Device is attached, so nothing reports for the Asset; it is kept off the map.</summary>
    NoDevice,

    /// <summary>At least one of the Asset's Alerts is Active.</summary>
    Alerting,

    /// <summary>Its Device has sent nothing for <see cref="AssetStatuses.OfflineAfter"/>.</summary>
    Offline,

    /// <summary>Reporting, and nothing needs attention.</summary>
    Normal,
}
