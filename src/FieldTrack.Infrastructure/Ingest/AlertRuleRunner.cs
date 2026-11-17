using FieldTrack.Domain.Alerts;
using Npgsql;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// Runs the rules engine inside an ingest batch's transaction (ADR 0005): evaluates the enabled Alert Rules against
/// the Readings the batch stored and writes the Alerts, resolutions and rule state with them. Keeps the enabled rules
/// and the rule state in memory and reloads both whenever the Alert Rule version has moved; one per
/// <see cref="ReadingWriter"/>, which is a single instance (spec #21).
/// </summary>
public sealed class AlertRuleRunner
{
    /// <summary>
    /// Reads the Alert Rule version; <see cref="ReadingWriter"/> runs it in each batch before storing and hands it to
    /// <see cref="ReloadCommands"/>. The share lock makes a change to rules or rule state (which bumps the version
    /// before touching Alerts or rule state) wait for the batch to commit, and the batch wait for a change that got
    /// there first, so a change never interleaves with a batch: it sees the batch's Alerts, or the batch sees the
    /// change (ADR 0005).
    /// </summary>
    public const string ReadVersion = "SELECT version FROM alert_rule_version FOR SHARE";

    private const string LoadRules = """
        SELECT id, name, kind, asset_id, asset_group_id, severity, sensor, operator, threshold, hysteresis
        FROM alert_rules WHERE enabled
        """;

    private const string LoadStates = """
        SELECT alert_rule_id, asset_id, breached, last_device_id, last_boot, last_sequence_number FROM alert_rule_states
        """;

    private const string ResolvedAlertId = "resolved_alert_id";

    // Conditional, so a manual resolve that got there first wins (#10). Returns the id of the Alert it resolved, for
    // the live push.
    private const string ResolveActive = $"""
        UPDATE alerts SET status = 'Resolved', resolved_at = @at, resolve_reason = 'Cleared'
        WHERE alert_rule_id = @rule AND asset_id = @asset AND status <> 'Resolved'
        RETURNING id AS {ResolvedAlertId}
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

    private readonly TimeProvider _time;
    private Dictionary<(Guid Rule, Guid Asset), RuleState> _states = [];
    private List<AlertRule> _rules = [];
    private long? _version;

    /// <summary>A runner that loads the rules and rule state in its first batch; <paramref name="time"/> stamps the Alerts it creates.</summary>
    public AlertRuleRunner(TimeProvider time) => _time = time;

    /// <summary>
    /// The statements that load the rules and rule state when <paramref name="version"/>, read by
    /// <see cref="ReadVersion"/>, is not the one loaded last; none when it is. Run them in the batch's transaction, after
    /// <see cref="ReadVersion"/>, and hand their results to <see cref="ReloadAsync"/>.
    /// </summary>
    public NpgsqlBatchCommand[] ReloadCommands(long version) => version == _version ? [] : [new(LoadRules), new(LoadStates)];

    /// <summary>
    /// Takes the rules and rule state of <see cref="ReloadCommands"/> from <paramref name="reader"/>, positioned on the
    /// first of their result sets. Both are committed rows, so keeping them is safe even if the batch then fails.
    /// </summary>
    public async Task ReloadAsync(NpgsqlDataReader reader, long version, CancellationToken cancellationToken)
    {
        var rules = await ReadRulesAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var states = new Dictionary<(Guid Rule, Guid Asset), RuleState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            states[(reader.GetGuid(0), reader.GetGuid(1))] =
                new RuleState(reader.GetBoolean(2), new ReadingId(reader.GetGuid(3), reader.GetInt32(4), reader.GetInt64(5)));
        }

        (_rules, _states, _version) = (rules, states, version);
    }

    /// <summary>
    /// Evaluates the enabled rules against <paramref name="stored"/>, the Readings a batch stored for Assets in use, in
    /// receive order, and writes the outcome in <paramref name="transaction"/>.
    /// </summary>
    public async Task<RuleOutcome> EvaluateAsync(
        NpgsqlTransaction transaction, IReadOnlyList<AssetReading> stored, CancellationToken cancellationToken)
    {
        var changed = new Dictionary<(Guid Rule, Guid Asset), RuleState>();
        List<Guid> raised = [];
        List<Guid> resolved = [];
        await using var writes = new NpgsqlBatch(transaction.Connection, transaction);
        foreach (var asset in stored.GroupBy(reading => (reading.AssetId, reading.AssetGroupId)))
        {
            var (assetId, assetGroupId) = asset.Key;
            foreach (var rule in _rules.Where(rule => rule.Targets(assetId, assetGroupId)))
            {
                var key = (rule.Id, assetId);
                var state = _states.GetValueOrDefault(key);
                var evaluation = RuleEvaluator.Evaluate(rule, assetId, state, asset.Select(reading => reading.Reading), _time);
                // Resolve before inserting: the unique index allows one Active Alert per rule and Asset.
                if (evaluation.ResolveActiveAlertAt is { } resolveAt)
                {
                    writes.BatchCommands.Add(Command(ResolveActive, ("at", resolveAt), ("rule", rule.Id), ("asset", assetId)));
                }

                foreach (var alert in evaluation.Raised)
                {
                    writes.BatchCommands.Add(Insert(alert));
                    raised.Add(alert.Id);
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
            await using var reader = await writes.ExecuteReaderAsync(cancellationToken);
            do
            {
                // Read by column name, so rows another command might return are never taken for resolved Alerts.
                if (reader.FieldCount == 0 || reader.GetName(0) != ResolvedAlertId)
                {
                    continue;
                }

                while (await reader.ReadAsync(cancellationToken))
                {
                    resolved.Add(reader.GetGuid(0));
                }
            }
            while (await reader.NextResultAsync(cancellationToken));
        }

        return new RuleOutcome(changed, raised, resolved);
    }

    /// <summary>Takes the rule states of a committed batch into memory.</summary>
    public void Keep(IReadOnlyDictionary<(Guid Rule, Guid Asset), RuleState> committed)
    {
        foreach (var (key, state) in committed)
        {
            _states[key] = state;
        }
    }

    private static async Task<List<AlertRule>> ReadRulesAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var rules = new List<AlertRule>();
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
                // ck_alert_rules_threshold keeps the four settings all set and in range, or all null.
                ThresholdSettings = reader.IsDBNull(6)
                    ? null
                    : new ThresholdSettings(Enum.Parse<Sensor>(reader.GetString(6)), Enum.Parse<ThresholdOperator>(reader.GetString(7)),
                        reader.GetDouble(8), reader.GetDouble(9)),
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
