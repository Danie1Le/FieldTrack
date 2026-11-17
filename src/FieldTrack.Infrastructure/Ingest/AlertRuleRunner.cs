using FieldTrack.Domain.Alerts;
using Npgsql;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// Runs the rules engine inside an ingest batch's transaction (ADR 0005): evaluates the enabled Alert Rules against
/// the Readings the batch stored and writes the Alerts, resolutions and rule state with them. Keeps the rule state
/// in memory, loaded when ingest opens; one per <see cref="ReadingWriter"/>, which is a single instance (spec #21).
/// </summary>
public sealed class AlertRuleRunner
{
    private const string LoadStates = """
        SELECT alert_rule_id, asset_id, breached, last_device_id, last_boot, last_sequence_number FROM alert_rule_states
        """;

    // shortcut: rules are read again every batch that stored Readings, so a new or edited rule applies from the
    // next batch; #34 swaps this for ADR 0005's version row. A rule deleted after this read fails the batch on the
    // foreign key of its Alert or rule state, and the redelivered batch reads the rules again.
    private const string LoadRules = """
        SELECT id, name, kind, asset_id, asset_group_id, severity, sensor, operator, threshold, hysteresis
        FROM alert_rules WHERE enabled
        """;

    // Conditional, so a manual resolve that got there first wins (#10).
    private const string ResolveActive = """
        UPDATE alerts SET status = 'Resolved', resolved_at = @at, resolve_reason = 'Cleared'
        WHERE alert_rule_id = @rule AND asset_id = @asset AND status <> 'Resolved'
        """;

    private const string InsertAlert = """
        INSERT INTO alerts (id, alert_rule_id, asset_id, reading_device_id, reading_boot, reading_sequence_number,
                            severity, message, status, raised_at, resolved_at, resolve_reason, created_at, from_backlog)
        VALUES (@id, @rule, @asset, @device, @boot, @seq, @severity, @message, @status, @raised_at, @resolved_at,
                @resolve_reason, @created_at, @from_backlog)
        """;

    private const string SaveState = """
        INSERT INTO alert_rule_states (alert_rule_id, asset_id, breached, last_device_id, last_boot, last_sequence_number)
        VALUES (@rule, @asset, @breached, @device, @boot, @seq)
        ON CONFLICT (alert_rule_id, asset_id) DO UPDATE
        SET breached = excluded.breached, last_device_id = excluded.last_device_id,
            last_boot = excluded.last_boot, last_sequence_number = excluded.last_sequence_number
        """;

    private readonly Dictionary<(Guid Rule, Guid Asset), RuleState> _states;
    private readonly TimeProvider _time;

    private AlertRuleRunner(Dictionary<(Guid Rule, Guid Asset), RuleState> states, TimeProvider time) =>
        (_states, _time) = (states, time);

    /// <summary>Loads every stored rule state over <paramref name="connection"/>.</summary>
    public static async Task<AlertRuleRunner> LoadAsync(NpgsqlConnection connection, TimeProvider time, CancellationToken cancellationToken)
    {
        var states = new Dictionary<(Guid Rule, Guid Asset), RuleState>();
        await using var command = new NpgsqlCommand(LoadStates, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states[(reader.GetGuid(0), reader.GetGuid(1))] =
                new RuleState(reader.GetBoolean(2), new ReadingId(reader.GetGuid(3), reader.GetInt32(4), reader.GetInt64(5)));
        }

        return new AlertRuleRunner(states, time);
    }

    /// <summary>
    /// Evaluates the enabled rules against <paramref name="stored"/>, the Readings a batch stored for Assets in use, in
    /// receive order, and writes the outcome in <paramref name="transaction"/>. Returns the rule states to keep once
    /// the transaction commits; pass them to <see cref="Keep"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<(Guid Rule, Guid Asset), RuleState>> EvaluateAsync(
        NpgsqlTransaction transaction, IReadOnlyList<AssetReading> stored, CancellationToken cancellationToken)
    {
        var changed = new Dictionary<(Guid Rule, Guid Asset), RuleState>();
        if (stored.Count == 0)
        {
            return changed;
        }

        var rules = await LoadRulesAsync(transaction, cancellationToken);
        await using var writes = new NpgsqlBatch(transaction.Connection, transaction);
        foreach (var asset in stored.GroupBy(reading => (reading.AssetId, reading.AssetGroupId)))
        {
            var (assetId, assetGroupId) = asset.Key;
            foreach (var rule in rules.Where(rule => rule.Targets(assetId, assetGroupId)))
            {
                var key = (rule.Id, assetId);
                var state = _states.GetValueOrDefault(key);
                var evaluation = RuleEvaluator.Evaluate(rule, assetId, state, asset.Select(reading => reading.Reading), _time);
                // Resolve before inserting: the unique index allows one Active Alert per rule and Asset.
                if (evaluation.ActiveAlertClearedAt is { } clearedAt)
                {
                    writes.BatchCommands.Add(Command(ResolveActive, ("at", clearedAt), ("rule", rule.Id), ("asset", assetId)));
                }

                foreach (var alert in evaluation.Raised)
                {
                    writes.BatchCommands.Add(Insert(alert));
                }

                if (evaluation.State is { } newState && newState != state)
                {
                    changed[key] = newState;
                    writes.BatchCommands.Add(Command(SaveState,
                        ("rule", rule.Id), ("asset", assetId), ("breached", newState.Breached), ("device", newState.LastEvaluated.DeviceId),
                        ("boot", newState.LastEvaluated.Boot), ("seq", newState.LastEvaluated.SequenceNumber)));
                }
            }
        }

        if (writes.BatchCommands.Count > 0)
        {
            await writes.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    /// <summary>Takes the rule states of a committed batch into memory.</summary>
    public void Keep(IReadOnlyDictionary<(Guid Rule, Guid Asset), RuleState> committed)
    {
        foreach (var (key, state) in committed)
        {
            _states[key] = state;
        }
    }

    private static async Task<List<AlertRule>> LoadRulesAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rules = new List<AlertRule>();
        await using var command = new NpgsqlCommand(LoadRules, transaction.Connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new AlertRule
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Kind = Enum.Parse<AlertRuleKind>(reader.GetString(2)),
                AssetId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                AssetGroupId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                Severity = Enum.Parse<Severity>(reader.GetString(5)),
                Enabled = true,
                Sensor = reader.IsDBNull(6) ? null : Enum.Parse<Sensor>(reader.GetString(6)),
                Operator = reader.IsDBNull(7) ? null : Enum.Parse<ThresholdOperator>(reader.GetString(7)),
                Threshold = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                Hysteresis = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            });
        }

        return rules;
    }

    private static NpgsqlBatchCommand Insert(Alert alert) =>
        Command(InsertAlert,
            ("id", alert.Id), ("rule", alert.AlertRuleId), ("asset", alert.AssetId),
            ("device", alert.Reading.DeviceId), ("boot", alert.Reading.Boot), ("seq", alert.Reading.SequenceNumber),
            ("severity", alert.Severity.ToString()), ("message", alert.Message), ("status", alert.Status.ToString()),
            ("raised_at", alert.RaisedAt), ("resolved_at", (object?)alert.ResolvedAt ?? DBNull.Value),
            ("resolve_reason", (object?)alert.ResolveReason?.ToString() ?? DBNull.Value),
            ("created_at", alert.CreatedAt), ("from_backlog", alert.FromBacklog));

    private static NpgsqlBatchCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
