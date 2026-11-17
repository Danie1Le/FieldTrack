using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// Stores batches of Readings exactly once (ADR 0003, ADR 0004), and the batch's Rejected messages with them, so
/// stored plus rejected always equals published (spec #21), and evaluates Alert Rules against the new Readings in
/// the same transaction (ADR 0005). It holds one connection, with its stage table and rule state, for the life of
/// an ingest session; dispose it and open a new one after any failure.
/// </summary>
public sealed class ReadingWriter : IAsyncDisposable
{
    // ADR 0004 consequence: hand-written SQL; it must follow the readings and reading_keys tables in the
    // Readings migration. An Attachment covers [attached_at, detached_at), so a zero-length one covers nothing.
    private const string Covers = "a.attached_at <= s.device_time AND (a.detached_at IS NULL OR s.device_time < a.detached_at)";

    private const string CreateStage = """
        CREATE TEMP TABLE IF NOT EXISTS ingest_stage (
            ordinal integer NOT NULL,
            topic text NOT NULL,
            serial_number text NOT NULL,
            boot integer NOT NULL,
            seq bigint NOT NULL,
            device_time timestamptz NOT NULL,
            received_time timestamptz NOT NULL,
            lat double precision NOT NULL,
            lon double precision NOT NULL,
            speed_kph real NULL,
            engine_temp_c real NULL,
            fuel_pct real NULL,
            vibration_mm_s real NULL,
            battery_v real NULL,
            payload bytea NOT NULL,
            redelivered boolean NOT NULL
        ) ON COMMIT DELETE ROWS;
        CREATE TEMP TABLE IF NOT EXISTS reject_stage (
            topic text NOT NULL,
            payload bytea NOT NULL,
            reason text NOT NULL,
            received_time timestamptz NOT NULL,
            redelivered boolean NOT NULL
        ) ON COMMIT DELETE ROWS
        """;

    private const string Copy = """
        COPY ingest_stage (ordinal, topic, serial_number, boot, seq, device_time, received_time, lat, lon,
                           speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v, payload, redelivered)
        FROM STDIN (FORMAT BINARY)
        """;

    private const string CopyRejected = """
        COPY reject_stage (topic, payload, reason, received_time, redelivered) FROM STDIN (FORMAT BINARY)
        """;

    // Readings have no foreign keys, and Asset and Device delete decide between remove and Archive by
    // asking whether Readings exist (#24, #25). Lock what this batch stamps first, in id order and Device
    // before Asset like the API: a delete that got there first has then committed and its rows are gone
    // from the next statement's snapshot, and a delete that comes later waits and sees these Readings.
    private const string LockDevices = """
        SELECT FROM devices WHERE serial_number IN (SELECT serial_number FROM ingest_stage) ORDER BY id FOR NO KEY UPDATE
        """;

    private const string LockAssets = $"""
        SELECT FROM assets WHERE id IN (
            SELECT a.asset_id
            FROM ingest_stage s
            JOIN devices d ON d.serial_number = s.serial_number
            JOIN attachments a ON a.device_id = d.id AND {Covers})
        ORDER BY id FOR KEY SHARE
        """;

    // One statement: new keys go into reading_keys, and only the Readings whose key was new are stored,
    // so the first stored copy wins, across batches and within one (lowest ordinal = received first).
    // The content hash is SHA-256 over the raw payload: a Device resends a Reading byte for byte, so "identical
    // duplicate" (ADR 0003) means an identical payload, and any other copy is conflicting.
    // Telemetry from unknown or Archived Devices becomes Rejected messages, decided in this same statement so
    // each message is stored or rejected against one snapshot of devices, never both or neither. They are stored
    // with the batch's other Rejected messages, the topic clipped to RejectedMessage.MaxTopicLength.
    // A crash between commit and PUBACK makes the broker redeliver the batch, marked DUP. A redelivered Rejected
    // message is skipped when the same topic and payload was rejected in the last hour (the broker session's
    // expiry, so no older copy can be redelivered), so stored plus rejected still equals published (ADR 0004).
    // Among overlapping ended Attachments, the one attached last wins (#25).
    // Stamping is one-shot, from the Attachments committed when the Reading is stored, and is never redone.
    // Device time may run up to TelemetryPayload.MaxAhead past Received time, so a Reading near an attach
    // or detach can land on the neighbouring side: stored before an attach that covers it, it keeps no Asset;
    // stored before a detach that ends before its Device time, it keeps the old Asset.
    private static readonly string _store = $"""
        WITH staged AS (
            SELECT DISTINCT ON (d.id, s.boot, s.seq) d.id AS device_id, s.*
            FROM ingest_stage s
            JOIN devices d ON d.serial_number = s.serial_number
            WHERE NOT d.archived
            ORDER BY d.id, s.boot, s.seq, s.ordinal
        ), new_keys AS (
            INSERT INTO reading_keys (device_id, boot, seq, device_time, content_hash)
            SELECT device_id, boot, seq, device_time, sha256(payload) FROM staged
            ON CONFLICT DO NOTHING
            RETURNING device_id, boot, seq
        ), stored AS (
            INSERT INTO readings (device_id, boot, seq, asset_id, device_time, received_time, location,
                                  speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v)
            SELECT s.device_id, s.boot, s.seq,
                   (SELECT a.asset_id FROM attachments a
                    WHERE a.device_id = s.device_id AND {Covers}
                    ORDER BY a.attached_at DESC, a.id DESC
                    LIMIT 1),
                   s.device_time, s.received_time, ST_SetSRID(ST_MakePoint(s.lon, s.lat), 4326)::geography,
                   s.speed_kph, s.engine_temp_c, s.fuel_pct, s.vibration_mm_s, s.battery_v
            FROM staged s
            JOIN new_keys USING (device_id, boot, seq)
            RETURNING device_id, boot, seq, asset_id
        ), rejected AS (
            INSERT INTO rejected_messages (topic, topic_clipped, payload, reason, received_time)
            SELECT left(r.topic, {RejectedMessage.MaxTopicLength}), char_length(r.topic) > {RejectedMessage.MaxTopicLength},
                   r.payload, r.reason, r.received_time
            FROM (
                SELECT topic, payload, reason, received_time, redelivered FROM reject_stage
                UNION ALL
                SELECT s.topic, s.payload,
                       CASE WHEN d.id IS NULL THEN '{nameof(RejectReason.UnknownSerialNumber)}' ELSE '{nameof(RejectReason.ArchivedDevice)}' END,
                       s.received_time, s.redelivered
                FROM ingest_stage s
                LEFT JOIN devices d ON d.serial_number = s.serial_number
                WHERE d.id IS NULL OR d.archived
            ) r
            WHERE NOT (r.redelivered AND EXISTS (
                SELECT FROM rejected_messages m
                WHERE m.topic = left(r.topic, {RejectedMessage.MaxTopicLength})
                  AND m.received_time > r.received_time - interval '1 hour'
                  AND m.payload = r.payload))
            ORDER BY r.received_time
            RETURNING reason
        ), last_received AS (
            UPDATE devices d
            SET last_received_at = r.received_time
            FROM (SELECT device_id, max(received_time) AS received_time FROM staged GROUP BY device_id) r
            WHERE d.id = r.device_id
        ), evaluated AS (
            -- The new Readings of Assets in use, for rule evaluation (ADR 0005). A backlog Reading stamped with an
            -- Asset Archived since is stored but evaluates no rule.
            SELECT s.ordinal, s.device_id, r.asset_id, a.asset_group_id, s.boot, s.seq, s.device_time, s.lat, s.lon,
                   s.speed_kph, s.engine_temp_c, s.fuel_pct, s.vibration_mm_s, s.battery_v
            FROM stored r
            JOIN staged s USING (device_id, boot, seq)
            JOIN assets a ON a.id = r.asset_id
            WHERE NOT a.archived
        ), duplicates AS (
            -- Every copy of a known Device's Reading that was not stored, and whether its content hash matches the
            -- stored copy's (ADR 0003): an earlier batch's key (this statement's snapshot predates new_keys), or else
            -- this batch's first copy. The Device lock above means no other batch can store the key meanwhile.
            SELECT sha256(s.payload) = coalesce(k.content_hash, sha256(f.payload)) AS identical
            FROM ingest_stage s
            JOIN staged f ON f.serial_number = s.serial_number AND f.boot = s.boot AND f.seq = s.seq
            LEFT JOIN reading_keys k ON k.device_id = f.device_id AND k.boot = f.boot AND k.seq = f.seq
            WHERE s.ordinal <> f.ordinal OR k.device_id IS NOT NULL
        )
        -- Every kind of row from one statement (FULL JOIN ON false is a typed UNION), with nulls in the other kinds'
        -- columns: each stored Rejected message's reason and each duplicate's hash match, in no particular order,
        -- then each Reading to evaluate, in receive order.
        SELECT j.reason, u.identical, e.*
        FROM rejected j FULL JOIN duplicates u ON false FULL JOIN evaluated e ON false
        ORDER BY e.ordinal NULLS FIRST
        """;

    // One row of _store's final SELECT; edit the two together. Column 2 is the ordinal, used only for order.
    private static AssetReading ReadEvaluated(NpgsqlDataReader reader)
    {
        var reading = new Reading(
            reader.GetInt32(6), reader.GetInt64(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetDouble(9), reader.GetDouble(10),
            Sensor(11), Sensor(12), Sensor(13), Sensor(14), Sensor(15));
        return new AssetReading(reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetGuid(5), new StoredReading(reader.GetGuid(3), reading));

        float? Sensor(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFloat(ordinal);
    }

    private readonly NpgsqlConnection _connection;
    private readonly AlertRuleRunner _rules;
    private readonly IIngestCrashPoints? _crashes;

    private ReadingWriter(NpgsqlConnection connection, AlertRuleRunner rules, IIngestCrashPoints? crashes) =>
        (_connection, _rules, _crashes) = (connection, rules, crashes);

    /// <summary>
    /// Opens a connection from <paramref name="dataSource"/> and prepares its stage table; the first batch loads the
    /// rules and rule state.
    /// <paramref name="time"/> stamps the Alerts it creates; <paramref name="crashes"/> is the crash tests' hook.
    /// </summary>
    public static async Task<ReadingWriter> OpenAsync(
        NpgsqlDataSource dataSource, TimeProvider time, CancellationToken cancellationToken, IIngestCrashPoints? crashes = null)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(CreateStage, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new ReadingWriter(connection, new AlertRuleRunner(time), crashes);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stores <paramref name="readings"/> (in receive order) and <paramref name="rejected"/> in one transaction, and
    /// stamps each Reading with its Asset; Readings already stored are skipped, Readings from unknown or Archived
    /// Devices become Rejected messages, and redelivered Rejected messages already stored are skipped. The Alert Rules
    /// are evaluated against the newly stored Readings, and their Alerts and rule state commit with them. Returns once
    /// committed, with what the batch stored besides its Readings, how many copies it dropped as duplicates, and what
    /// it changed for the live push.
    /// </summary>
    public async Task<WrittenBatch> WriteAsync(
        IReadOnlyList<ReceivedReading> readings, IReadOnlyList<ReceivedRejectedMessage> rejected, CancellationToken cancellationToken)
    {
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        if (rejected.Count > 0)
        {
            await using var import = await _connection.BeginBinaryImportAsync(CopyRejected, cancellationToken);
            foreach (var message in rejected)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(message.Topic, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(message.Payload, NpgsqlDbType.Bytea, cancellationToken);
                await import.WriteAsync(message.Reason.ToString(), NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(message.ReceivedTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await import.WriteAsync(message.Redelivered, NpgsqlDbType.Boolean, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        await using (var import = await _connection.BeginBinaryImportAsync(Copy, cancellationToken))
        {
            for (var ordinal = 0; ordinal < readings.Count; ordinal++)
            {
                var (topic, serialNumber, reading, receivedTime, payload, redelivered) = readings[ordinal];
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(ordinal, NpgsqlDbType.Integer, cancellationToken);
                await import.WriteAsync(topic, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(serialNumber, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(reading.Boot, NpgsqlDbType.Integer, cancellationToken);
                await import.WriteAsync(reading.SequenceNumber, NpgsqlDbType.Bigint, cancellationToken);
                await import.WriteAsync(reading.DeviceTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await import.WriteAsync(receivedTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await import.WriteAsync(reading.Latitude, NpgsqlDbType.Double, cancellationToken);
                await import.WriteAsync(reading.Longitude, NpgsqlDbType.Double, cancellationToken);
                foreach (var sensor in (float?[])[reading.SpeedKph, reading.EngineTempC, reading.FuelPct, reading.VibrationMmS, reading.BatteryV])
                {
                    await import.WriteAsync(sensor, NpgsqlDbType.Real, cancellationToken);
                }

                await import.WriteAsync(payload, NpgsqlDbType.Bytea, cancellationToken);
                await import.WriteAsync(redelivered, NpgsqlDbType.Boolean, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        _crashes?.Reached(IngestCrashPoint.AfterStage);

        // Separate statements, so each sees what committed while the one before waited on a lock. The Alert Rule
        // version comes last, like in the API's changes (after their Device or Asset), so the two never deadlock.
        long version;
        await using (var locks = new NpgsqlBatch(_connection, transaction) { BatchCommands = { new(LockDevices), new(LockAssets), new(AlertRuleRunner.ReadVersion) } })
        await using (var reader = await locks.ExecuteReaderAsync(cancellationToken))
        {
            // Skip the two lock statements' result sets, which have no columns, to reach the version's.
            await reader.NextResultAsync(cancellationToken);
            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            version = reader.GetInt64(0);
        }

        // Store, then reload the rules if the version moved, in the same round trip (ADR 0005).
        var reasons = new List<RejectReason>();
        var (identical, conflicting) = (0, 0);
        var stored = new List<AssetReading>();
        var reload = _rules.ReloadCommands(version);
        await using (var store = new NpgsqlBatch(_connection, transaction) { BatchCommands = { new(_store) } })
        {
            foreach (var command in reload)
            {
                store.BatchCommands.Add(command);
            }

            await using var reader = await store.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    reasons.Add(Enum.Parse<RejectReason>(reader.GetString(0)));
                }
                else if (reader.IsDBNull(1))
                {
                    stored.Add(ReadEvaluated(reader));
                }
                else if (reader.GetBoolean(1))
                {
                    identical++;
                }
                else
                {
                    conflicting++;
                }
            }

            // The reload's result sets follow Store's only when ReloadCommands added its statements for this version.
            if (reload.Length > 0)
            {
                await reader.NextResultAsync(cancellationToken);
                await _rules.ReloadAsync(reader, version, cancellationToken);
            }
        }

        var outcome = await _rules.EvaluateAsync(transaction, stored, cancellationToken);
        _crashes?.Reached(IngestCrashPoint.AfterEvaluation);
        await transaction.CommitAsync(cancellationToken);
        _rules.Keep(outcome.States);
        return new WrittenBatch(
            reasons, identical, conflicting, [.. stored.Select(reading => reading.AssetId).Distinct()], outcome.Raised, outcome.Resolved);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
