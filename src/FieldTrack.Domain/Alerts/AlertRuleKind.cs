namespace FieldTrack.Domain.Alerts;

/// <summary>What an Alert Rule watches. Each kind has its own settings on <see cref="AlertRule"/> and its own evaluation.</summary>
public enum AlertRuleKind
{
    /// <summary>A sensor value past a threshold, with Hysteresis before it clears.</summary>
    Threshold,

    /// <summary>An Asset seen inside the rule's Geofence and then outside it.</summary>
    GeofenceExit,

    /// <summary>An Asset seen outside the rule's Geofence and then inside it.</summary>
    GeofenceEnter,

    /// <summary>No Reading received from the Asset's Device for the rule's own number of seconds.</summary>
    Offline,
}
