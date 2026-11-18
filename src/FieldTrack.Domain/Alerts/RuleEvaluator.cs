using System.Globalization;
using FieldTrack.Domain.Assets;
using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Domain.Alerts;

/// <summary>
/// The rules engine (ADR 0005): given an Alert Rule, one Asset's rule state and that Asset's newly stored
/// Readings, works out the new rule state and the Alerts to raise and resolve. No I/O, so ingest can run it
/// inside the batch transaction and tests can drive it directly. It takes a whole batch rather than one Reading so
/// that a backlog's breach can be raised and resolved together. It has two entry points, one per trigger:
/// <see cref="Evaluate"/> for a batch's Readings, which every rule kind goes through, and <see cref="Sweep"/> for the
/// sweep tick, which only Offline rules use, since silence has no Reading to evaluate (ADR 0005).
/// </summary>
public static class RuleEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="rule"/> for the Asset <paramref name="assetId"/> against <paramref name="readings"/>,
    /// the Asset's Readings stored by one ingest batch, Reading by Reading.
    /// </summary>
    /// <param name="rule">An enabled rule that targets the Asset.</param>
    /// <param name="assetId">The Asset the Readings are stamped with.</param>
    /// <param name="attachedDeviceId">
    /// The Device attached to the Asset now, or null if none. Only its Readings end an Offline rule's silence: a former
    /// Device's late backlog says nothing about the Device the sweep watches.
    /// </param>
    /// <param name="state">The rule state for this rule and Asset, or null before its first evaluation.</param>
    /// <param name="readings">The Readings, in receive order.</param>
    /// <param name="time">The platform clock, for <see cref="Alert.CreatedAt"/>.</param>
    public static RuleEvaluation Evaluate(
        AlertRule rule, Guid assetId, Guid? attachedDeviceId, RuleState? state, IEnumerable<StoredReading> readings, TimeProvider time)
    {
        if (rule.Kind == AlertRuleKind.Offline)
        {
            // Spec #21: any stored Reading from the Device ends the silence, a straggler or late backlog included, so
            // Offline rules keep no order. Resolved at platform time, like the sweep that raised it; a Device time could
            // predate it.
            return state is { Breached: true } && readings.Any(reading => reading.DeviceId == attachedDeviceId)
                ? new RuleEvaluation(state with { Breached = false }, time.GetUtcNow(), [])
                : new RuleEvaluation(state, null, []);
        }

        DateTimeOffset? resolveActiveAlertAt = null;
        var raised = new List<Alert>();
        // ADR 0005: each Device's Readings in (Boot, Sequence number) order, never Device time. The Device evaluated
        // last goes first, so its stragglers are judged against its own last Reading; any other Device counts as newer.
        var lastDevice = state?.LastEvaluated?.DeviceId;
        var ordered = readings
            .GroupBy(stored => stored.DeviceId)
            .OrderBy(device => device.Key != lastDevice)
            .SelectMany(device => device.OrderBy(stored => stored.Reading.Boot).ThenBy(stored => stored.Reading.SequenceNumber));
        foreach (var stored in ordered)
        {
            // Stragglers are stored for history but change no rule state.
            if (!stored.Id.ComesAfter(state?.LastEvaluated))
            {
                continue;
            }

            if (Transition(rule, stored.Reading, state?.Breached) is not (var wasBreached, var breached))
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
                // A breach raised by an earlier Reading of these clears together with it (a backlog, or a blip shorter than one batch); otherwise
                // the breach predates them, and so does its Alert.
                if (raised.LastOrDefault() is { Status: AlertStatus.Open } backlog)
                {
                    backlog.Status = AlertStatus.Resolved;
                    backlog.ResolvedAt = stored.Reading.DeviceTime;
                    backlog.ResolveReason = ResolveReason.Cleared;
                    backlog.FromBacklog = backlog.ClearedFromBacklog(stored.Reading.DeviceTime);
                }
                else
                {
                    resolveActiveAlertAt = stored.Reading.DeviceTime;
                }
            }
        }

        return new RuleEvaluation(state, resolveActiveAlertAt, raised);
    }

    /// <summary>
    /// One sweep tick for <paramref name="rule"/> and the Asset <paramref name="assetId"/>: raises an Offline Alert, at
    /// the sweep time, once the Asset's Device has sent nothing for the rule's seconds. Other kinds change nothing.
    /// </summary>
    /// <param name="rule">An enabled rule that targets the Asset.</param>
    /// <param name="assetId">An Asset with a Device attached.</param>
    /// <param name="state">The rule state for this rule and Asset, or null before its first evaluation.</param>
    /// <param name="tick">The sweep time, the Asset's Device and how long the worker has been caught up.</param>
    /// <param name="time">The platform clock, for <see cref="Alert.CreatedAt"/>.</param>
    public static RuleEvaluation Sweep(AlertRule rule, Guid assetId, RuleState? state, SweepTick tick, TimeProvider time)
    {
        var now = tick.At;
        // A breached rule already raised, or was latched by a manual resolve, and waits for a Reading to clear it.
        // ADR 0005 restart grace: nothing is raised while ingest is still draining the broker backlog.
        if (rule is not { Kind: AlertRuleKind.Offline, OfflineSeconds: { } seconds } || state is { Breached: true } || tick.CaughtUpAt is not { } caughtUpAt)
        {
            return new RuleEvaluation(state, null, []);
        }

        // Silence counts as for Asset Status, and exactly the rule's seconds is Offline (>=), so the two agree; but never
        // from before ingest caught up, so a restart gives every Device the rule's seconds to report again.
        var silentSince = AssetStatuses.SilentSince(tick.AttachedAt, tick.LastReceivedAt);
        if (now - (caughtUpAt > silentSince ? caughtUpAt : silentSince) < TimeSpan.FromSeconds(seconds))
        {
            return new RuleEvaluation(state, null, []);
        }

        var alert = new Alert
        {
            Id = Guid.CreateVersion7(now),
            AlertRuleId = rule.Id,
            AssetId = assetId,
            Severity = rule.Severity,
            Message = string.Create(CultureInfo.InvariantCulture, $"No Reading received for {seconds} s"),
            Status = AlertStatus.Open,
            RaisedAt = now,
            CreatedAt = time.GetUtcNow(),
        };
        return new RuleEvaluation(new RuleState(true, state?.LastEvaluated), null, [alert]);
    }

    /// <summary>
    /// Whether the condition held before and holds after <paramref name="reading"/>, given the stored
    /// <paramref name="breached"/> (null when unknown), or null when the Reading changes nothing. Each kind decides
    /// what unknown means, so a kind can take its first Reading as a baseline instead of a transition.
    /// </summary>
    private static (bool Was, bool Now)? Transition(AlertRule rule, Reading reading, bool? breached) =>
        rule switch
        {
            { Kind: AlertRuleKind.Threshold, ThresholdSettings: { } threshold } => ThresholdTransition(threshold, reading, breached),
            { Kind: AlertRuleKind.GeofenceExit, Geofence: { } geofence } => GeofenceTransition(!geofence.Contains(reading.Latitude, reading.Longitude), breached),
            { Kind: AlertRuleKind.GeofenceEnter, Geofence: { } geofence } => GeofenceTransition(geofence.Contains(reading.Latitude, reading.Longitude), breached),
            // A kind without its settings evaluates nothing rather than fail the ingest batch; the schema refuses it.
            _ => null,
        };

    // #10: Hysteresis only. With > 105 and Hysteresis 5, breach above 105 and clear below 100; mirrored for <.
    // ADR 0005: unknown state counts as clear. A Reading without the sensor neither raises nor clears.
    private static (bool Was, bool Now)? ThresholdTransition(ThresholdSettings settings, Reading reading, bool? breached)
    {
        if (SensorOf(settings.Sensor).Value(reading) is not { } value)
        {
            return null;
        }

        // Compared at the float precision the Device sends, so a Reading of exactly 10.1 is not past a threshold of 10.1.
        var was = breached ?? false;
        var (threshold, hysteresis) = (settings.Threshold, settings.Hysteresis);
        var now = settings.Operator switch
        {
            ThresholdOperator.GreaterThan => was ? value >= (float)(threshold - hysteresis) : value > (float)threshold,
            ThresholdOperator.LessThan => was ? value <= (float)(threshold + hysteresis) : value < (float)threshold,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Operator, "Unknown threshold operator."),
        };
        return (was, now);
    }

    // Breached means on the side the rule alerts on: outside for exit, inside for enter. ADR 0005: the first Reading only
    // sets that baseline, so an Alert needs an observed crossing.
    private static (bool Was, bool Now) GeofenceTransition(bool onAlertingSide, bool? breached) => (breached ?? onAlertingSide, onAlertingSide);

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

    private static string Message(AlertRule rule, Reading reading) =>
        rule.Kind switch
        {
            AlertRuleKind.GeofenceExit => $"Left Geofence {rule.Geofence!.Name}",
            AlertRuleKind.GeofenceEnter => $"Entered Geofence {rule.Geofence!.Name}",
            _ => ThresholdMessage(rule.ThresholdSettings!, reading),
        };

    private static string ThresholdMessage(ThresholdSettings settings, Reading reading)
    {
        var (label, unit, value) = SensorOf(settings.Sensor);
        var (threshold, hysteresis) = (settings.Threshold, settings.Hysteresis);
        var (side, clears, clearsAt) = settings.Operator switch
        {
            ThresholdOperator.GreaterThan => ("above", "below", threshold - hysteresis),
            ThresholdOperator.LessThan => ("below", "above", threshold + hysteresis),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Operator, "Unknown threshold operator."),
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{label} {value(reading):0.##} {unit}, {side} {threshold:0.##} (clears {clears} {clearsAt:0.##})");
    }

    // The one table of sensors: how people read it and where a Reading carries it.
    private static (string Label, string Unit, Func<Reading, float?> Value) SensorOf(Sensor sensor) =>
        sensor switch
        {
            Sensor.SpeedKph => ("Speed", "km/h", reading => reading.SpeedKph),
            Sensor.EngineTempC => ("Engine temp", "°C", reading => reading.EngineTempC),
            Sensor.FuelPct => ("Fuel", "%", reading => reading.FuelPct),
            Sensor.VibrationMmS => ("Vibration", "mm/s", reading => reading.VibrationMmS),
            Sensor.BatteryV => ("Battery", "V", reading => reading.BatteryV),
            _ => throw new ArgumentOutOfRangeException(nameof(sensor), sensor, "Unknown sensor."),
        };
}
