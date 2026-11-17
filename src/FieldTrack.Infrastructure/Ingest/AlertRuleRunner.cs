using FieldTrack.Domain.Alerts;
using FieldTrack.Infrastructure.Persistence;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

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

    // Each rule with its Geofence, if any: Geofence edits bump the version too, so they reload with the rules.
    private const string LoadRules = """
        SELECT r.id, r.name, r.kind, r.asset_id, r.asset_group_id, r.severity, r.sensor, r.operator, r.threshold, r.hysteresis,
               g.id, g.name, ST_AsBinary(g.area), r.offline_seconds
        FROM alert_rules r LEFT JOIN geofences g ON g.id = r.geofence_id
        WHERE r.enabled
        """;

    // ADR 0005 lock order: a sweep that raises locks its Assets, then the version. Returns the Assets still in use.
    private const string LockSweptAssets = """
        SELECT a.id FROM assets a JOIN attachments t ON t.asset_id = a.id AND t.detached_at IS NULL
        WHERE a.id = ANY(@ids) AND NOT a.archived
        ORDER BY a.id FOR KEY SHARE OF a
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
            // ck_alert_rule_states_last_evaluated keeps the three columns all set or all null.
            states[(reader.GetGuid(0), reader.GetGuid(1))] = new RuleState(reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : new ReadingId(reader.GetGuid(3), reader.GetInt32(4), reader.GetInt64(5)));
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
        // One statement read every row, so an Asset's Readings agree on its group and attached Device.
        foreach (var asset in stored.GroupBy(reading => (reading.AssetId, reading.AssetGroupId, reading.AttachedDeviceId)))
        {
            var (assetId, assetGroupId, attachedDeviceId) = asset.Key;
            foreach (var rule in _rules.Where(rule => rule.Targets(assetId, assetGroupId)))
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
    /// Brings the enabled rules and rule state up to date for a sweep, over <paramref name="connection"/> outside any
    /// batch: reads the Alert Rule version and reloads both when it moved. A sweep may come before any batch, or after a
    /// change no batch has seen yet. Call it before reading what the sweep judges, so that is no older than the rules.
    /// </summary>
    public async Task RefreshAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        long version;
        await using (var read = new NpgsqlCommand(ReadVersion, connection))
        {
            version = (long)(await read.ExecuteScalarAsync(cancellationToken))!;
        }

        var reload = ReloadCommands(version);
        if (reload.Length == 0)
        {
            return;
        }

        await using var batch = new NpgsqlBatch(connection);
        foreach (var command in reload)
        {
            batch.BatchCommands.Add(command);
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        await ReloadAsync(reader, version, cancellationToken);
    }

    /// <summary>
    /// The sweep for Offline rules (ADR 0005), after <see cref="RefreshAsync"/>: checks the enabled rules against each of
    /// <paramref name="assets"/>, the Assets in use with a Device attached, and raises an Offline Alert for each that has
    /// been silent for the rule's seconds at <paramref name="at"/>, the sweep time, in a transaction of its own over
    /// <paramref name="connection"/>. Nothing is raised before <paramref name="caughtUpAt"/>, when ingest caught up with the
    /// broker backlog. Returns the ids of the Alerts raised.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SweepAsync(
        NpgsqlConnection connection, IReadOnlyList<AttachedAsset> assets, DateTimeOffset at, DateTimeOffset? caughtUpAt,
        CancellationToken cancellationToken)
    {
        var raises = new List<(AlertRule Rule, Guid AssetId, RuleEvaluation Evaluation)>();
        foreach (var asset in assets)
        {
            var tick = new SweepTick(at, asset.AttachedAt, asset.LastReceivedAt, caughtUpAt);
            foreach (var rule in _rules.Where(rule => rule.Targets(asset.AssetId, asset.AssetGroupId)))
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

        // ADR 0005 lock order, as a batch: the Assets, then the version. Every detach, Archive and rule change bumps the
        // version before it touches Attachments, rules, Alerts or rule state, so if the version has not moved since the
        // refresh, nothing this sweep judged by has changed, and a change that comes later waits for this commit and sees
        // its Alert. If it has moved, raise nothing: the next sweep, a second later, reloads and judges again.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        HashSet<Guid> lockedAssets = [];
        long version;
        await using (var locks = new NpgsqlBatch(connection, transaction)
        {
            BatchCommands = { Ids(LockSweptAssets, raises.Select(raise => raise.AssetId)), new(ReadVersion) },
        })
        await using (var reader = await locks.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                lockedAssets.Add(reader.GetGuid(0));
            }

            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            version = reader.GetInt64(0);
        }

        if (version != _version)
        {
            return [];
        }

        var changed = new Dictionary<(Guid Rule, Guid Asset), RuleState>();
        List<Guid> raised = [];
        await using var writes = new NpgsqlBatch(connection, transaction);
        foreach (var (rule, assetId, evaluation) in raises.Where(raise => lockedAssets.Contains(raise.AssetId)))
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

    private static async Task<List<AlertRule>> ReadRulesAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var rules = new List<AlertRule>();
        // One instance per Geofence, so rules that share it share its prepared geometry (ADR 0005).
        var geofences = new Dictionary<Guid, Geofence>();
        while (await reader.ReadAsync(cancellationToken))
        {
            Geofence? geofence = null;
            if (!reader.IsDBNull(10) && !geofences.TryGetValue(reader.GetGuid(10), out geofence))
            {
                geofence = geofences[reader.GetGuid(10)] = new Geofence
                {
                    Id = reader.GetGuid(10),
                    Name = reader.GetString(11),
                    Area = (Polygon)new WKBReader().Read(reader.GetFieldValue<byte[]>(12)),
                };
            }

            rules.Add(new AlertRule
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Kind = Enum.Parse<AlertRuleKind>(reader.GetString(2)),
                AssetId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                AssetGroupId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                Severity = Enum.Parse<Severity>(reader.GetString(5)),
                Enabled = true,
                OfflineSeconds = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                // ck_alert_rules_threshold keeps the four settings all set and in range, or all null.
                ThresholdSettings = reader.IsDBNull(6)
                    ? null
                    : new ThresholdSettings(Enum.Parse<Sensor>(reader.GetString(6)), Enum.Parse<ThresholdOperator>(reader.GetString(7)),
                        reader.GetDouble(8), reader.GetDouble(9)),
                GeofenceId = geofence?.Id,
                Geofence = geofence,
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
