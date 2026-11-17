using System.Globalization;
using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Domain.Alerts;

/// <summary>
/// The rules engine (ADR 0005): given an Alert Rule, one Asset's rule state and that Asset's newly stored
/// Readings, works out the new rule state and the Alerts to raise and resolve. No I/O, so ingest can run it
/// inside the batch transaction and tests can drive it directly.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="rule"/> for the Asset <paramref name="assetId"/> against <paramref name="readings"/>,
    /// the Asset's Readings stored by one ingest batch, Reading by Reading.
    /// </summary>
    /// <param name="rule">An enabled rule that targets the Asset.</param>
    /// <param name="assetId">The Asset the Readings are stamped with.</param>
    /// <param name="state">The rule state for this rule and Asset, or null before its first evaluation.</param>
    /// <param name="readings">The Readings, in receive order.</param>
    /// <param name="time">The platform clock, for <see cref="Alert.CreatedAt"/>.</param>
    public static RuleEvaluation Evaluate(AlertRule rule, Guid assetId, RuleState? state, IEnumerable<StoredReading> readings, TimeProvider time)
    {
        DateTimeOffset? activeAlertClearedAt = null;
        var raised = new List<Alert>();
        // ADR 0005: each Device's Readings in (Boot, Sequence number) order, never Device time; GroupBy keeps the
        // Devices in arrival order, and a Device swap counts as newer.
        var ordered = readings
            .GroupBy(stored => stored.DeviceId)
            .SelectMany(device => device.OrderBy(stored => stored.Reading.Boot).ThenBy(stored => stored.Reading.SequenceNumber));
        foreach (var stored in ordered)
        {
            // Stragglers are stored for history but change no rule state.
            if (!stored.Id.ComesAfter(state?.LastEvaluated))
            {
                continue;
            }

            // Unknown state counts as clear.
            var wasBreached = state?.Breached ?? false;
            if (BreachedAfter(rule, stored.Reading, wasBreached) is not { } breached)
            {
                continue;
            }

            state = new RuleState(breached, stored.Id);
            if (breached && !wasBreached)
            {
                raised.Add(Raise(rule, assetId, stored, time));
            }
            else if (!breached && wasBreached)
            {
                // A breach raised by an earlier Reading of these clears together with it (a backlog); otherwise
                // the breach predates them, and so does its Alert.
                if (raised.LastOrDefault() is { Status: AlertStatus.Open } backlog)
                {
                    backlog.Status = AlertStatus.Resolved;
                    backlog.ResolvedAt = stored.Reading.DeviceTime;
                    backlog.ResolveReason = ResolveReason.Cleared;
                    backlog.FromBacklog = true;
                }
                else
                {
                    activeAlertClearedAt = stored.Reading.DeviceTime;
                }
            }
        }

        return new RuleEvaluation(state, activeAlertClearedAt, raised);
    }

    /// <summary>Whether the condition holds after <paramref name="reading"/>, or null when the Reading changes nothing.</summary>
    private static bool? BreachedAfter(AlertRule rule, Reading reading, bool breached) =>
        rule.Kind switch
        {
            AlertRuleKind.Threshold => ThresholdBreachedAfter(rule, reading, breached),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Kind, "Unknown Alert Rule kind."),
        };

    // #10: deadband only. With > 105 and Hysteresis 5, breach above 105 and clear below 100; mirrored for <.
    // A Reading without the sensor neither raises nor clears.
    private static bool? ThresholdBreachedAfter(AlertRule rule, Reading reading, bool breached)
    {
        if (ValueOf(rule.Sensor!.Value, reading) is not { } value)
        {
            return null;
        }

        var (threshold, hysteresis) = (rule.Threshold!.Value, rule.Hysteresis!.Value);
        return rule.Operator switch
        {
            ThresholdOperator.GreaterThan => breached ? value >= threshold - hysteresis : value > threshold,
            ThresholdOperator.LessThan => breached ? value <= threshold + hysteresis : value < threshold,
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Operator, "Unknown threshold operator."),
        };
    }

    private static Alert Raise(AlertRule rule, Guid assetId, StoredReading stored, TimeProvider time)
    {
        var createdAt = time.GetUtcNow();
        return new Alert
        {
            Id = Guid.CreateVersion7(createdAt),
            AlertRuleId = rule.Id,
            AssetId = assetId,
            Reading = stored.Id,
            Severity = rule.Severity,
            Message = Message(rule, stored.Reading),
            Status = AlertStatus.Open,
            RaisedAt = stored.Reading.DeviceTime,
            CreatedAt = createdAt,
        };
    }

    private static string Message(AlertRule rule, Reading reading)
    {
        var (label, unit) = rule.Sensor switch
        {
            Sensor.SpeedKph => ("Speed", "km/h"),
            Sensor.EngineTempC => ("Engine temp", "°C"),
            Sensor.FuelPct => ("Fuel", "%"),
            Sensor.VibrationMmS => ("Vibration", "mm/s"),
            Sensor.BatteryV => ("Battery", "V"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Sensor, "Unknown sensor."),
        };
        var (threshold, hysteresis) = (rule.Threshold!.Value, rule.Hysteresis!.Value);
        var (side, clears, clearsAt) = rule.Operator == ThresholdOperator.GreaterThan
            ? ("above", "below", threshold - hysteresis)
            : ("below", "above", threshold + hysteresis);
        return string.Create(CultureInfo.InvariantCulture,
            $"{label} {ValueOf(rule.Sensor!.Value, reading):0.##} {unit}, {side} {threshold:0.##} (clears {clears} {clearsAt:0.##})");
    }

    private static double? ValueOf(Sensor sensor, Reading reading) =>
        sensor switch
        {
            Sensor.SpeedKph => reading.SpeedKph,
            Sensor.EngineTempC => reading.EngineTempC,
            Sensor.FuelPct => reading.FuelPct,
            Sensor.VibrationMmS => reading.VibrationMmS,
            Sensor.BatteryV => reading.BatteryV,
            _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, "Unknown sensor."),
        };
}
