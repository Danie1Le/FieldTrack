using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// Streams one Asset's Readings as CSV straight from Postgres (#42): <c>COPY ... TO STDOUT</c> formats every Reading in
/// the range, never thinned, and the bytes go to the caller's stream as they arrive, so 31 days of a busy Asset
/// (over 10 million Readings at demo rate) never sit in memory. Running exports are counted per user with
/// session-level advisory locks, which work across replicas and vanish with a dropped connection.
/// </summary>
public static class ReadingCsvExport
{
    /// <summary>The most exports one user runs at once (spec #21).</summary>
    public const int MaxRunningPerUser = 2;

    // Every time is UTC in ISO 8601 with a Z (spec #21).
    private const int BufferSize = 64 * 1024;

    private const string IsoUtc = "YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"";

    /// <summary>How many exports <paramref name="userId"/> is running now, on any host.</summary>
    public static Task<int> RunningAsync(FieldTrackDbContext db, Guid userId, CancellationToken cancellationToken) =>
        // A two-key advisory lock shows its keys as classid (unsigned) and objid, with objsubid 2.
        db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_locks
            WHERE locktype = 'advisory' AND granted AND objsubid = 2
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND classid::bigint = (hashtext({userId.ToString()})::bigint & 4294967295) AND objid::bigint < {MaxRunningPerUser}
            """).SingleAsync(cancellationToken);

    /// <summary>
    /// Takes one of <paramref name="userId"/>'s export slots on <paramref name="connection"/>'s session, or returns null
    /// when all <see cref="MaxRunningPerUser"/> are taken. Dispose the slot when the export ends.
    /// </summary>
    public static async Task<IAsyncDisposable?> TryStartAsync(NpgsqlConnection connection, Guid userId, CancellationToken cancellationToken)
    {
        for (var slot = 0; slot < MaxRunningPerUser; slot++)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1), $2)", connection)
            {
                Parameters = { new() { Value = userId.ToString() }, new() { Value = slot } },
            };
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                return new Slot(connection, userId, slot);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the Readings stamped with <paramref name="assetId"/> whose Device time is in [<paramref name="from"/>,
    /// <paramref name="to"/>) to <paramref name="destination"/> as CSV with a header row, ordered by Device time.
    /// </summary>
    public static async Task CopyAsync(
        NpgsqlConnection connection, Guid assetId, DateTimeOffset from, DateTimeOffset to, Stream destination, CancellationToken cancellationToken)
    {
        // COPY takes no parameters. Only a Guid and UTC times formatted here reach the SQL, never text a user typed.
        // A serial starting with - (the only formula character a serial may hold) is prefixed with ' so a spreadsheet
        // reads it as text (CSV injection); Asset names are not in the file.
        var sql = string.Create(CultureInfo.InvariantCulture, $"""
            COPY (
                SELECT CASE WHEN d.serial_number ~ '^[-=+@]' THEN '''' || d.serial_number ELSE d.serial_number END AS device_serial,
                       r.boot, r.seq,
                       to_char(r.device_time AT TIME ZONE 'UTC', '{IsoUtc}') AS device_time,
                       to_char(r.received_time AT TIME ZONE 'UTC', '{IsoUtc}') AS received_time,
                       ST_Y(r.location::geometry) AS lat, ST_X(r.location::geometry) AS lon,
                       r.speed_kph, r.engine_temp_c, r.fuel_pct, r.vibration_mm_s, r.battery_v
                FROM readings r
                JOIN devices d ON d.id = r.device_id
                WHERE r.asset_id = '{assetId}' AND r.device_time >= '{from.UtcDateTime:O}' AND r.device_time < '{to.UtcDateTime:O}'
                ORDER BY r.device_time, r.device_id, r.boot, r.seq
            ) TO STDOUT WITH (FORMAT csv, HEADER)
            """);
        // Npgsql's raw copy is binary only; a text export decodes and the writer encodes again, one buffer at a time.
        using var csv = await connection.BeginTextExportAsync(sql, cancellationToken);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), BufferSize, leaveOpen: true);
        var buffer = new char[BufferSize];
        int read;
        while ((read = await csv.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class Slot(NpgsqlConnection connection, Guid userId, int slot) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // A broken connection has ended its session, and the lock with it.
            if (connection.FullState != ConnectionState.Open)
            {
                return;
            }

            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext($1), $2)", connection)
            {
                Parameters = { new() { Value = userId.ToString() }, new() { Value = slot } },
            };
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
    }
}
