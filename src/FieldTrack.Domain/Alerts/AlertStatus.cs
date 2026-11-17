namespace FieldTrack.Domain.Alerts;

/// <summary>Where an Alert is in its lifecycle (#10). It only moves forward, and Resolved is final.</summary>
public enum AlertStatus
{
    /// <summary>Raised and not yet seen; Active.</summary>
    Open,

    /// <summary>Seen by a user but not ended; Active.</summary>
    Acknowledged,

    /// <summary>Ended by the platform or a user; never reopens.</summary>
    Resolved,
}
