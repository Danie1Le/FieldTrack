using FieldTrack.Domain.Alerts;
using Npgsql;
using NpgsqlTypes;

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

    /// <summary>
    /// Reads the enabled rules; <see cref="ReadingWriter"/> runs it in its batch, after storing, and hands the rows to
    /// <see cref="ReadRulesAsync"/>. The key share lock makes a rule delete wait for the batch to commit, so the delete
    /// then sees the batch's Alert (409) or deletes cleanly, instead of failing the batch on a foreign key.
    /// </summary>
    // shortcut: rules are read again every batch, so a new or edited rule applies from the next batch; #34 swaps this
    // for ADR 0005's version row. Fine for Threshold rules only: Geofence rules (#32, after #34) must not rebuild
    // their prepared geometries per batch.
    public const string LoadRules = $"SELECT {RuleColumns} FROM alert_rules WHERE enabled FOR KEY SHARE";

    /// <summary>
    /// Reads the enabled Offline rules for <see cref="SweepAsync"/>, unlocked: most sweeps raise nothing, and the one
    /// that does locks what it raises for.
    /// </summary>
    // shortcut: read every sweep, like LoadRules every batch, until #34's version row lets the worker keep rules in memory.
    public const string LoadOfflineRules = $"SELECT {RuleColumns} FROM alert_rules WHERE enabled AND kind = '{nameof(AlertRuleKind.Offline)}'";

    private const string RuleColumns = "id, name, kind, asset_id, asset_group_id, severity, sensor, operator, threshold, hysteresis, offline_seconds";

    // Locks what a sweep's Alerts reference, Assets before rules like a batch, and returns those still in use and
    // enabled: an Archive, detach, delete or disable that committed since the sweep read wins, and one that comes later
    // waits for the sweep and sees its Alert.
    private const string LockSweptAssets = """
        SELECT a.id FROM assets a JOIN attachments t ON t.asset_id = a.id AND t.detached_at IS NULL
        WHERE a.id = ANY(@ids) AND NOT a.archived
        ORDER BY a.id FOR KEY SHARE OF a
        """;

    private const string LockSweptRules = $"""
        SELECT id FROM alert_rules WHERE id = ANY(@ids) AND enabled AND kind = '{nameof(AlertRuleKind.Offline)}'
        ORDER BY id FOR KEY SHARE
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
            // ck_alert_rule_states_last_evaluated keeps the three columns all set or all null.
            states[(reader.GetGuid(0), reader.GetGuid(1))] = new RuleState(reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : new ReadingId(reader.GetGuid(3), reader.GetInt32(4), reader.GetInt64(5)));
        }

        return new AlertRuleRunner(states, time);
    }

    /// <summary>
    /// Evaluates <paramref name="rules"/>, the enabled rules, against <paramref name="stored"/>, the Readings a batch
    /// stored for Assets in use, in receive order, and writes the outcome in <paramref name="transaction"/>.
    /// </summary>
    public async Task<RuleOutcome> EvaluateAsync(
        NpgsqlTransaction transaction, IReadOnlyList<AlertRule> rules, IReadOnlyList<AssetReading> stored, CancellationToken cancellationToken)
    {
        var changed = new Dictionary<(Guid Rule, Guid Asset), RuleState>();
        List<Guid> raised = [];
        List<Guid> resolved = [];
        await using var writes = new NpgsqlBatch(transaction.Connection, transaction);
        // One statement read every row, so an Asset's Readings agree on its group and attached Device.
        foreach (var asset in stored.GroupBy(reading => (reading.AssetId, reading.AssetGroupId, reading.AttachedDeviceId)))
        {
            var (assetId, assetGroupId, attachedDeviceId) = asset.Key;
            foreach (var rule in rules.Where(rule => rule.Targets(assetId, assetGroupId)))
            {
                var key = (rule.Id, assetId);
                var state = _states.GetValueOrDefault(key);
                var evaluation = RuleEvaluator.Evaluate(rule, assetId, attachedDeviceId, state, asset.Select(reading => reading.Reading), _time);
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
                    writes.BatchCommands.Add(Save(rule.Id, assetId, newState));
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

    /// <summary>
    /// The sweep for Offline rules (ADR 0005): checks <paramref name="rules"/>, the enabled Offline rules, against each
    /// of <paramref name="assets"/>, the Assets in use with a Device attached, and raises an Offline Alert for each that
    /// has been silent for the rule's seconds, in a transaction of its own over <paramref name="connection"/>. Nothing is
    /// raised before <paramref name="caughtUpAt"/>, when ingest caught up with the broker backlog. Returns the ids of the
    /// Alerts raised.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SweepAsync(
        NpgsqlConnection connection, IReadOnlyList<AlertRule> rules, IReadOnlyList<AttachedAsset> assets, DateTimeOffset? caughtUpAt,
        CancellationToken cancellationToken)
    {
        var raises = new List<(AlertRule Rule, Guid AssetId, RuleEvaluation Evaluation)>();
        foreach (var asset in assets)
        {
            var tick = new SweepTick(asset.AttachedAt, asset.LastReceivedAt, caughtUpAt);
            foreach (var rule in rules.Where(rule => rule.Targets(asset.AssetId, asset.AssetGroupId)))
            {
                var evaluation = RuleEvaluator.Sweep(rule, asset.AssetId, _states.GetValueOrDefault((rule.Id, asset.AssetId)), tick, _time);
                if (evaluation.Raised.Count > 0)
                {
                    raises.Add((rule, asset.AssetId, evaluation));
                }
            }
        }

        if (raises.Count == 0)
        {
            return [];
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        HashSet<Guid> lockedAssets = [], lockedRules = [];
        await using (var locks = new NpgsqlBatch(connection, transaction)
        {
            BatchCommands =
            {
                Ids(LockSweptAssets, raises.Select(raise => raise.AssetId)),
                Ids(LockSweptRules, raises.Select(raise => raise.Rule.Id)),
            },
        })
        await using (var reader = await locks.ExecuteReaderAsync(cancellationToken))
        {
            foreach (var locked in (HashSet<Guid>[])[lockedAssets, lockedRules])
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    locked.Add(reader.GetGuid(0));
                }

                await reader.NextResultAsync(cancellationToken);
            }
        }

        var changed = new Dictionary<(Guid Rule, Guid Asset), RuleState>();
        List<Guid> raised = [];
        await using var writes = new NpgsqlBatch(connection, transaction);
        foreach (var (rule, assetId, evaluation) in raises.Where(raise => lockedAssets.Contains(raise.AssetId) && lockedRules.Contains(raise.Rule.Id)))
        {
            foreach (var alert in evaluation.Raised)
            {
                writes.BatchCommands.Add(Insert(alert));
                raised.Add(alert.Id);
            }

            changed[(rule.Id, assetId)] = evaluation.State!;
            writes.BatchCommands.Add(Save(rule.Id, assetId, evaluation.State!));
        }

        if (writes.BatchCommands.Count > 0)
        {
            await writes.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Keep(changed);
        return raised;
    }

    /// <summary>Takes the rule states of a committed batch into memory.</summary>
    public void Keep(IReadOnlyDictionary<(Guid Rule, Guid Asset), RuleState> committed)
    {
        foreach (var (key, state) in committed)
        {
            _states[key] = state;
        }
    }

    /// <summary>Reads the rows of <see cref="LoadRules"/> or <see cref="LoadOfflineRules"/> from the current result set of <paramref name="reader"/>.</summary>
    public static async Task<List<AlertRule>> ReadRulesAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
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
                OfflineSeconds = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                // ck_alert_rules_threshold keeps the four settings all set and in range, or all null.
                ThresholdSettings = reader.IsDBNull(6)
                    ? null
                    : new ThresholdSettings(Enum.Parse<Sensor>(reader.GetString(6)), Enum.Parse<ThresholdOperator>(reader.GetString(7)),
                        reader.GetDouble(8), reader.GetDouble(9)),
            });
        }

        return rules;
    }

    private static NpgsqlBatchCommand Save(Guid ruleId, Guid assetId, RuleState state) =>
        Command(SaveState,
            ("rule", ruleId), ("asset", assetId), ("breached", state.Breached),
            ("device", (object?)state.LastEvaluated?.DeviceId ?? DBNull.Value), ("boot", (object?)state.LastEvaluated?.Boot ?? DBNull.Value),
            ("seq", (object?)state.LastEvaluated?.SequenceNumber ?? DBNull.Value));

    private static NpgsqlBatchCommand Insert(Alert alert) =>
        Command(InsertAlert,
            ("id", alert.Id), ("rule", alert.AlertRuleId), ("asset", alert.AssetId),
            ("device", (object?)alert.Reading?.DeviceId ?? DBNull.Value), ("boot", (object?)alert.Reading?.Boot ?? DBNull.Value),
            ("seq", (object?)alert.Reading?.SequenceNumber ?? DBNull.Value),
            ("severity", alert.Severity.ToString()), ("message", alert.Message), ("status", alert.Status.ToString()),
            ("raised_at", alert.RaisedAt), ("resolved_at", (object?)alert.ResolvedAt ?? DBNull.Value),
            ("resolve_reason", (object?)alert.ResolveReason?.ToString() ?? DBNull.Value),
            ("created_at", alert.CreatedAt), ("from_backlog", alert.FromBacklog));

    private static NpgsqlBatchCommand Ids(string sql, IEnumerable<Guid> ids)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids.Distinct().ToArray());
        return command;
    }

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
