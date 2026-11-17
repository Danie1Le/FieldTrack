using Npgsql;
using NpgsqlTypes;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// Stores batches of Readings exactly once (ADR 0003, ADR 0004). It holds one connection, with its stage
/// table, for the life of an ingest session; dispose it and open a new one after any failure.
/// </summary>
public sealed class ReadingWriter : IAsyncDisposable
{
    // ADR 0004 consequence: hand-written SQL; it must follow the readings and reading_keys tables in the
    // Readings migration. An Attachment covers [attached_at, detached_at), so a zero-length one covers nothing.
    private const string Covers = "a.attached_at <= s.device_time AND (a.detached_at IS NULL OR s.device_time < a.detached_at)";

    private const string CreateStage = """
        CREATE TEMP TABLE IF NOT EXISTS ingest_stage (
            ordinal integer NOT NULL,
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
            content_hash bytea NOT NULL
        ) ON COMMIT DELETE ROWS
        """;

    private const string Copy = """
        COPY ingest_stage (ordinal, serial_number, boot, seq, device_time, received_time, lat, lon,
                           speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v, content_hash)
        FROM STDIN (FORMAT BINARY)
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
    // Telemetry from unknown or Archived Devices is skipped (#27 records it as Rejected messages).
    // Among overlapping ended Attachments, the one attached last wins (#25).
    // Stamping is one-shot, from the Attachments committed when the Reading is stored, and is never redone.
    // Device time may run up to TelemetryPayload.MaxAhead past Received time, so a Reading near an attach
    // or detach can land on the neighbouring side: stored before an attach that covers it, it keeps no Asset;
    // stored before a detach that ends before its Device time, it keeps the old Asset.
    private const string Store = $"""
        WITH staged AS (
            SELECT DISTINCT ON (d.id, s.boot, s.seq) d.id AS device_id, s.*
            FROM ingest_stage s
            JOIN devices d ON d.serial_number = s.serial_number
            WHERE NOT d.archived
            ORDER BY d.id, s.boot, s.seq, s.ordinal
        ), new_keys AS (
            INSERT INTO reading_keys (device_id, boot, seq, device_time, content_hash)
            SELECT device_id, boot, seq, device_time, content_hash FROM staged
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
        )
        UPDATE devices d
        SET last_received_at = r.received_time
        FROM (SELECT device_id, max(received_time) AS received_time FROM staged GROUP BY device_id) r
        WHERE d.id = r.device_id
        """;

    private readonly NpgsqlConnection _connection;

    private ReadingWriter(NpgsqlConnection connection) => _connection = connection;

    /// <summary>Opens a connection from <paramref name="dataSource"/> and prepares its stage table.</summary>
    public static async Task<ReadingWriter> OpenAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(CreateStage, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new ReadingWriter(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stores <paramref name="readings"/> (in receive order) in one transaction and stamps each with its
    /// Asset; Readings already stored are skipped. Returns once committed.
    /// </summary>
    public async Task WriteAsync(IReadOnlyList<ReceivedReading> readings, CancellationToken cancellationToken)
    {
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        await using (var import = await _connection.BeginBinaryImportAsync(Copy, cancellationToken))
        {
            for (var ordinal = 0; ordinal < readings.Count; ordinal++)
            {
                var (serialNumber, reading, receivedTime, contentHash) = readings[ordinal];
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(ordinal, NpgsqlDbType.Integer, cancellationToken);
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

                await import.WriteAsync(contentHash, NpgsqlDbType.Bytea, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        // Separate statements, so each sees what committed while the one before waited on a lock.
        await using var batch = new NpgsqlBatch(_connection, transaction)
        {
            BatchCommands = { new(LockDevices), new(LockAssets), new(Store) },
        };
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
