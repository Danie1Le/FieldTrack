namespace FieldTrack.Domain.Alerts;

/// <summary>Which side of its threshold breaches a Threshold rule.</summary>
public enum ThresholdOperator
{
    /// <summary><c>&gt;</c>: breached above the threshold, clear again below threshold minus Hysteresis.</summary>
    GreaterThan,

    /// <summary><c>&lt;</c>: breached below the threshold, clear again above threshold plus Hysteresis.</summary>
    LessThan,
}
