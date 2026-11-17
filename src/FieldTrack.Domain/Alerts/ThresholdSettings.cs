namespace FieldTrack.Domain.Alerts;

/// <summary>
/// What a Threshold rule watches and where it breaches and clears. One value, refused when any part is unknown or
/// out of range, so a rule cannot be half set up and evaluation inside the ingest transaction (ADR 0005) cannot fail
/// on it.
/// </summary>
public sealed record ThresholdSettings
{
    /// <summary>Creates the settings; throws <see cref="ArgumentOutOfRangeException"/> when any part is unknown or out of range.</summary>
    public ThresholdSettings(Sensor sensor, ThresholdOperator @operator, double threshold, double hysteresis)
    {
        if (!Enum.IsDefined(sensor))
        {
            throw new ArgumentOutOfRangeException(nameof(sensor), sensor, "Unknown sensor.");
        }

        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unknown threshold operator.");
        }

        // NaN never breaches and an infinite margin never clears, so both would leave the rule silently broken.
        if (!double.IsFinite(threshold))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be a finite number.");
        }

        if (!double.IsFinite(hysteresis) || hysteresis < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hysteresis), hysteresis, "Hysteresis must be a finite number, 0 or more.");
        }

        (Sensor, Operator, Threshold, Hysteresis) = (sensor, @operator, threshold, hysteresis);
    }

    /// <summary>The sensor watched.</summary>
    public Sensor Sensor { get; }

    /// <summary>Which side of <see cref="Threshold"/> breaches.</summary>
    public ThresholdOperator Operator { get; }

    /// <summary>The value the sensor must pass to breach, in the sensor's unit.</summary>
    public double Threshold { get; }

    /// <summary>How far back past <see cref="Threshold"/> the value must move to clear (0 or more).</summary>
    public double Hysteresis { get; }
}
