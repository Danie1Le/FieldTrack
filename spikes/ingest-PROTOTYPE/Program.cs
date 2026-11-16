// PROTOTYPE, wipe me. Throwaway spike for the wayfinder ticket
// "Ingest spike: throughput and zero loss under crash". Never merged.
//
// Modes:
//   suite             parameter sweep, flood ceiling per write path, then the 10-minute chaos run
//   quick             short smoke run of both write paths (one with kills)
//   run  --path copy|ef --batch N --flush MS --rate R --seconds S --kills K --label L
//   worker ...        the ingest worker (started by the harness, killed hard with Process.Kill)

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var mode = args.Length > 0 ? args[0] : "suite";
var o = Spike.ParseArgs(args.Skip(1).ToArray());
switch (mode)
{
    case "worker": await Worker.Run(o); break;
    case "run": Console.WriteLine(Harness.Header); Console.WriteLine((await Harness.RunOne(Config.From(o))).Row()); break;
    case "quick": await Harness.Quick(); break;
    default: await Harness.Suite(); break;
}

static class Spike
{
    public const string Pg = "Host=localhost;Port=15432;Username=postgres;Password=spike;Database=postgres;Include Error Detail=true;Maximum Pool Size=20";
    public const string MqttHost = "localhost";
    public const int MqttPort = 11883;
    public const string TopicFilter = "fieldtrack/v1/devices/+/telemetry";
    public const string PgContainer = "fieldtrack-spike-pg-PROTOTYPE";
    public const string MqttContainer = "fieldtrack-spike-mqtt-PROTOTYPE";
    public const int Devices = 200;

    public static Dictionary<string, string> ParseArgs(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
        return d;
    }
}

record Config(string Path, int Batch, int FlushMs, int Rate, int Seconds, int Kills, string Label)
{
    public static Config From(Dictionary<string, string> o) => new(
        o["path"], int.Parse(o["batch"]), int.Parse(o["flush"]), int.Parse(o["rate"]),
        int.Parse(o["seconds"]), int.Parse(o.GetValueOrDefault("kills", "0")), o["label"]);
}

// ---------------------------------------------------------------- worker

sealed class Msg
{
    public MqttApplicationMessageReceivedEventArgs Ev = null!;
    public int DeviceId, Boot;
    public long Seq, PubTs, RecvMs;
    public DateTime DeviceTime;
    public double Lat, Lon;
    public float? Speed, Temp, Fuel, Vib, Batt;
    public byte[] Hash = null!;
}

static class Worker
{
    static long _received, _processed;
    public static async Task Run(Dictionary<string, string> o)
    {
        string path = o["path"], clientId = o["clientid"], latFile = o["latfile"];
        int batchSize = int.Parse(o["batch"]), flushMs = int.Parse(o["flush"]);

        var ch = Channel.CreateUnbounded<Msg>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        // Serial handler: parse, hand off, return. Never await the DB here.
        client.ApplicationMessageReceivedAsync += e =>
        {
            e.AutoAcknowledge = false;
            ch.Writer.TryWrite(Parse(e));
            Interlocked.Increment(ref _received);
            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(Spike.MqttHost, Spike.MqttPort)
            .WithClientId(clientId)
            .WithCleanStart(false)
            .WithSessionExpiryInterval(3600)
            .WithReceiveMaximum((ushort)Math.Min(65535, batchSize * 2))
            .Build();

        IBatchWriter writer = path == "copy" ? await CopyWriter.Create() : new EfWriter();

        var res = await client.ConnectAsync(options);
        await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(Spike.TopicFilter, MqttQualityOfServiceLevel.AtLeastOnce).Build());
        Console.WriteLine($"READY session_present={res.IsSessionPresent}");

        // Harness closes stdin for a graceful stop; a hard kill gives no warning at all.
        var stop = new CancellationTokenSource();
        _ = Task.Run(() => { Console.In.ReadToEnd(); stop.Cancel(); });

        using var lat = new BinaryWriter(File.Open(latFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        var reader = ch.Reader;
        var batch = new List<Msg>(batchSize);
        long flushTicks = flushMs * Stopwatch.Frequency / 1000;
        long stBatches = 0, stMsgs = 0, stRecvLag = 0; double stWriteMs = 0; var stClock = Stopwatch.StartNew();

        while (!stop.IsCancellationRequested)
        {
            try { if (!await reader.WaitToReadAsync(stop.Token)) break; }
            catch (OperationCanceledException) { break; }

            // Batch closes on size, or flushMs after its first message.
            var deadline = Stopwatch.GetTimestamp() + flushTicks;
            batch.Clear();
            while (true)
            {
                while (batch.Count < batchSize && reader.TryRead(out var m)) batch.Add(m);
                if (batch.Count >= batchSize) break;
                long remainMs = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (remainMs <= 0) break;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                cts.CancelAfter((int)remainMs);
                try { await reader.WaitToReadAsync(cts.Token); } catch (OperationCanceledException) { break; }
            }

            var wsw = Stopwatch.StartNew();
            await writer.Write(batch); // throws -> process dies -> broker redelivers on reconnect
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _processed += batch.Count;
            stBatches++; stMsgs += batch.Count; stWriteMs += wsw.Elapsed.TotalMilliseconds;
            foreach (var m in batch) stRecvLag += m.RecvMs - m.PubTs;
            if (stClock.Elapsed.TotalSeconds >= 10)
            {
                // publish->receive lag splits broker/transport time from batching + DB time
                Console.WriteLine($"STAT batches={stBatches} avg_batch={stMsgs / (double)stBatches:F0} avg_write_ms={stWriteMs / stBatches:F1} avg_recv_lag_ms={stRecvLag / (double)stMsgs:F1} queued={Interlocked.Read(ref _received) - _processed}");
                stBatches = stMsgs = stRecvLag = 0; stWriteMs = 0; stClock.Restart();
            }

            // PUBACK only after commit, in receive order (channel order == handler order).
            foreach (var m in batch)
            {
                try { await m.Ev.AcknowledgeAsync(CancellationToken.None); } catch { /* broker redelivers */ }
                lat.Write(m.PubTs);
                lat.Write((int)(nowMs - m.PubTs));
            }
            lat.Flush();
        }
        await client.DisconnectAsync();
    }

    static Msg Parse(MqttApplicationMessageReceivedEventArgs e)
    {
        var bytes = e.ApplicationMessage.Payload.ToArray();
        var serial = e.ApplicationMessage.Topic.Split('/')[3];
        using var doc = JsonDocument.Parse(bytes);
        var r = doc.RootElement;
        var s = r.GetProperty("sensors");
        return new Msg
        {
            Ev = e,
            DeviceId = int.Parse(serial.AsSpan(4)), // "dev-00042" -> 42 (spike shortcut for serial lookup)
            Boot = r.GetProperty("boot").GetInt32(),
            Seq = r.GetProperty("seq").GetInt64(),
            DeviceTime = r.GetProperty("ts").GetDateTimeOffset().UtcDateTime,
            Lat = r.GetProperty("lat").GetDouble(),
            Lon = r.GetProperty("lon").GetDouble(),
            Speed = F(s, "speed_kph"), Temp = F(s, "engine_temp_c"), Fuel = F(s, "fuel_pct"),
            Vib = F(s, "vibration_mm_s"), Batt = F(s, "battery_v"),
            PubTs = r.GetProperty("pub_ts").GetInt64(),
            RecvMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Hash = SHA256.HashData(bytes),
        };
    }

    static float? F(JsonElement s, string name) => s.TryGetProperty(name, out var v) ? v.GetSingle() : null;
}

interface IBatchWriter { Task Write(List<Msg> batch); }

// Path A: binary COPY into a temp staging table, then key-table insert and reading insert in one statement.
sealed class CopyWriter : IBatchWriter
{
    NpgsqlConnection _c = null!;

    const string InsertSql = """
        WITH s AS (SELECT DISTINCT ON (device_id, boot, seq) * FROM stage ORDER BY device_id, boot, seq),
        k AS (
          INSERT INTO reading_key (device_id, boot, seq, device_time, content_hash)
          SELECT device_id, boot, seq, device_time, hash FROM s
          ON CONFLICT DO NOTHING
          RETURNING device_id, boot, seq)
        INSERT INTO reading (device_id, boot, seq, asset_id, device_time, received_time, location,
                             speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v)
        SELECT s.device_id, s.boot, s.seq, s.device_id, s.device_time, clock_timestamp(),
               ST_SetSRID(ST_MakePoint(s.lon, s.lat), 4326)::geography,
               s.speed_kph, s.engine_temp_c, s.fuel_pct, s.vibration_mm_s, s.battery_v
        FROM s JOIN k USING (device_id, boot, seq)
        """;

    public static async Task<CopyWriter> Create()
    {
        var w = new CopyWriter { _c = new NpgsqlConnection(Spike.Pg) };
        await w._c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            CREATE TEMP TABLE stage (device_id int, boot int, seq bigint, device_time timestamptz,
              lat float8, lon float8, speed_kph real, engine_temp_c real, fuel_pct real,
              vibration_mm_s real, battery_v real, hash bytea) ON COMMIT DELETE ROWS
            """, w._c);
        await cmd.ExecuteNonQueryAsync();
        return w;
    }

    public async Task Write(List<Msg> batch)
    {
        await using var tx = await _c.BeginTransactionAsync();
        await using (var imp = await _c.BeginBinaryImportAsync(
            "COPY stage (device_id, boot, seq, device_time, lat, lon, speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v, hash) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var m in batch)
            {
                await imp.StartRowAsync();
                await imp.WriteAsync(m.DeviceId, NpgsqlDbType.Integer);
                await imp.WriteAsync(m.Boot, NpgsqlDbType.Integer);
                await imp.WriteAsync(m.Seq, NpgsqlDbType.Bigint);
                await imp.WriteAsync(m.DeviceTime, NpgsqlDbType.TimestampTz);
                await imp.WriteAsync(m.Lat, NpgsqlDbType.Double);
                await imp.WriteAsync(m.Lon, NpgsqlDbType.Double);
                await W(imp, m.Speed); await W(imp, m.Temp); await W(imp, m.Fuel); await W(imp, m.Vib); await W(imp, m.Batt);
                await imp.WriteAsync(m.Hash, NpgsqlDbType.Bytea);
            }
            await imp.CompleteAsync();
        }
        await using (var cmd = new NpgsqlCommand(InsertSql, _c, tx)) await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    static Task W(NpgsqlBinaryImporter imp, float? v) =>
        v is { } f ? imp.WriteAsync(f, NpgsqlDbType.Real) : imp.WriteNullAsync();
}

// Path B: idiomatic EF Core 10. Single writer, so check-then-insert is race-free.
sealed class EfWriter : IBatchWriter
{
    readonly DbContextOptions<SpikeDb> _opts = new DbContextOptionsBuilder<SpikeDb>()
        .UseNpgsql(Spike.Pg, o => o.UseNetTopologySuite()).Options;

    public async Task Write(List<Msg> batch)
    {
        await using var db = new SpikeDb(_opts);
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var devs = batch.Select(m => m.DeviceId).ToArray();
        var boots = batch.Select(m => m.Boot).ToArray();
        var seqs = batch.Select(m => m.Seq).ToArray();
        var existing = await db.Keys
            .FromSqlInterpolated($"SELECT k.* FROM reading_key k JOIN unnest({devs}, {boots}, {seqs}) AS u(d, b, s) ON k.device_id = u.d AND k.boot = u.b AND k.seq = u.s")
            .AsNoTracking().Select(k => new { k.DeviceId, k.Boot, k.Seq }).ToListAsync();
        var seen = existing.Select(k => (k.DeviceId, k.Boot, k.Seq)).ToHashSet();

        var now = DateTime.UtcNow;
        foreach (var m in batch)
        {
            if (!seen.Add((m.DeviceId, m.Boot, m.Seq))) continue; // already stored, or repeated inside this batch
            db.Keys.Add(new ReadingKey { DeviceId = m.DeviceId, Boot = m.Boot, Seq = m.Seq, DeviceTime = m.DeviceTime, ContentHash = m.Hash });
            db.Readings.Add(new Reading
            {
                DeviceId = m.DeviceId, Boot = m.Boot, Seq = m.Seq, AssetId = m.DeviceId,
                DeviceTime = m.DeviceTime, ReceivedTime = now,
                Location = new Point(m.Lon, m.Lat) { SRID = 4326 },
                SpeedKph = m.Speed, EngineTempC = m.Temp, FuelPct = m.Fuel, VibrationMmS = m.Vib, BatteryV = m.Batt,
            });
        }
        await db.SaveChangesAsync();
    }
}

sealed class ReadingKey
{
    public int DeviceId { get; set; }
    public int Boot { get; set; }
    public long Seq { get; set; }
    public DateTime DeviceTime { get; set; }
    public byte[] ContentHash { get; set; } = null!;
}

sealed class Reading
{
    public int DeviceId { get; set; }
    public int Boot { get; set; }
    public long Seq { get; set; }
    public int AssetId { get; set; }
    public DateTime DeviceTime { get; set; }
    public DateTime ReceivedTime { get; set; }
    public Point Location { get; set; } = null!;
    public float? SpeedKph { get; set; }
    public float? EngineTempC { get; set; }
    public float? FuelPct { get; set; }
    public float? VibrationMmS { get; set; }
    public float? BatteryV { get; set; }
}

sealed class SpikeDb(DbContextOptions<SpikeDb> o) : DbContext(o)
{
    public DbSet<ReadingKey> Keys => Set<ReadingKey>();
    public DbSet<Reading> Readings => Set<Reading>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ReadingKey>(e =>
        {
            e.ToTable("reading_key");
            e.HasKey(x => new { x.DeviceId, x.Boot, x.Seq });
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Boot).HasColumnName("boot");
            e.Property(x => x.Seq).HasColumnName("seq");
            e.Property(x => x.DeviceTime).HasColumnName("device_time");
            e.Property(x => x.ContentHash).HasColumnName("content_hash");
        });
        b.Entity<Reading>(e =>
        {
            e.ToTable("reading");
            e.HasKey(x => new { x.DeviceId, x.Boot, x.Seq }); // EF-side only; uniqueness lives in reading_key
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Boot).HasColumnName("boot");
            e.Property(x => x.Seq).HasColumnName("seq");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.DeviceTime).HasColumnName("device_time");
            e.Property(x => x.ReceivedTime).HasColumnName("received_time");
            e.Property(x => x.Location).HasColumnName("location").HasColumnType("geography (point, 4326)");
            e.Property(x => x.SpeedKph).HasColumnName("speed_kph");
            e.Property(x => x.EngineTempC).HasColumnName("engine_temp_c");
            e.Property(x => x.FuelPct).HasColumnName("fuel_pct");
            e.Property(x => x.VibrationMmS).HasColumnName("vibration_mm_s");
            e.Property(x => x.BatteryV).HasColumnName("battery_v");
        });
    }
}

// ---------------------------------------------------------------- publisher

static class Publisher
{
    // rate 0 = flood (each device publishes as fast as its PUBACKs return).
    public static async Task<(long[] Counts, double Secs)> Run(int rate, int seconds)
    {
        var factory = new MqttClientFactory();
        const int nClients = 20;
        var clients = new IMqttClient[nClients];
        for (int i = 0; i < nClients; i++)
        {
            clients[i] = factory.CreateMqttClient();
            await clients[i].ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer(Spike.MqttHost, Spike.MqttPort)
                .WithClientId($"spike-pub-{i}-{Guid.NewGuid():N}")
                .WithCleanStart(true).Build());
        }

        var counts = new long[Spike.Devices];
        var sw = Stopwatch.StartNew();
        var end = TimeSpan.FromSeconds(seconds);
        var tasks = Enumerable.Range(0, Spike.Devices).Select(d => Task.Run(async () =>
        {
            var c = clients[d % nClients];
            var topic = $"fieldtrack/v1/devices/dev-{d + 1:D5}/telemetry";
            using var timer = rate > 0 ? new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 * Spike.Devices / rate)) : null;
            var rnd = new Random(d);
            double lat0 = 51 + rnd.NextDouble(), lon0 = -1 + rnd.NextDouble();
            long seq = 0;
            while (sw.Elapsed < end)
            {
                if (timer != null) await timer.WaitForNextTickAsync();
                var now = DateTimeOffset.UtcNow;
                var payload =
                    $"{{\"boot\":1,\"seq\":{seq},\"ts\":\"{now:yyyy-MM-ddTHH:mm:ss.fffZ}\",\"lat\":{lat0 + seq * 1e-5:F6},\"lon\":{lon0:F6}," +
                    $"\"sensors\":{{\"speed_kph\":{rnd.NextDouble() * 80:F1},\"engine_temp_c\":{80 + rnd.NextDouble() * 20:F1},\"fuel_pct\":{50 + rnd.NextDouble() * 10:F1}," +
                    $"\"vibration_mm_s\":{rnd.NextDouble() * 5:F2},\"battery_v\":{12 + rnd.NextDouble():F2}}},\"pub_ts\":{now.ToUnixTimeMilliseconds()}}}";
                var msg = new MqttApplicationMessageBuilder().WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
                while (true)
                {
                    try { if ((await c.PublishAsync(msg)).IsSuccess) break; }
                    catch { }
                    await Task.Delay(100); // same bytes again; an identical duplicate if the first actually landed
                }
                counts[d] = ++seq;
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        var secs = sw.Elapsed.TotalSeconds;
        foreach (var c in clients) { await c.DisconnectAsync(); c.Dispose(); }
        return (counts, secs);
    }
}

// ---------------------------------------------------------------- harness

record Result(Config C, long Published, double PubRate, long Stored, long Missing, long Duplicates, long Unexpected,
    long P50, long P95, long P99, long Max, double IngestRate, double DrainSecs, int BrokerDrops, int Kills, int Crashes)
{
    public bool Clean => Missing == 0 && Duplicates == 0 && Unexpected == 0 && BrokerDrops == 0;
    public string Row() =>
        $"| {C.Label} | {C.Path} | {C.Batch} | {C.FlushMs} | {(C.Rate == 0 ? "flood" : C.Rate)} | {C.Seconds} | {Kills} | {Crashes} | {Published} | {PubRate:F0} | {Stored} | {Missing} | {Duplicates} | {BrokerDrops} | {P50} | {P95} | {P99} | {Max} | {IngestRate:F0} | {DrainSecs:F1} |";
}

static class Harness
{
    public const string Header =
        "| run | path | batch | flush ms | rate | secs | kills | crashes | published | pub/s | stored | missing | dup | broker drops | p50 ms | p95 ms | p99 ms | max ms | ingest/s | drain s |\n" +
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

    static readonly string OutDir = Path.GetFullPath("out");

    public static async Task Quick()
    {
        Directory.CreateDirectory(OutDir);
        var results = new List<Result>
        {
            await RunOne(new Config("copy", 1000, 100, 2000, 20, 2, "quick-copy-kill")),
            await RunOne(new Config("ef", 1000, 100, 2000, 25, 0, "quick-ef")),
            await RunOne(new Config("copy", 1000, 100, 2000, 25, 0, "quick-copy")),
        };
        Report("quick", results);
    }

    public static async Task Suite()
    {
        Directory.CreateDirectory(OutDir);
        var results = new List<Result>();
        int[][] grid = [[250, 100], [500, 100], [1000, 100], [2000, 100], [1000, 50], [1000, 25], [1000, 250]];

        foreach (var path in new[] { "copy", "ef" })
            foreach (var g in grid)
            {
                results.Add(await RunOne(new Config(path, g[0], g[1], 2000, 180, 0, $"{path}-b{g[0]}-f{g[1]}")));
                Report("suite", results);
            }

        Result? Best(string? path) => results
            .Where(r => r.C.Rate == 2000 && r.C.Kills == 0 && r.Clean && r.DrainSecs < 5 && (path == null || r.C.Path == path))
            .OrderBy(r => r.P95).FirstOrDefault();

        foreach (var path in new[] { "copy", "ef" })
        {
            var b = Best(path)?.C ?? new Config(path, 1000, 100, 2000, 180, 0, "");
            results.Add(await RunOne(b with { Rate = 0, Seconds = 60, Label = $"{path}-flood-b{b.Batch}-f{b.FlushMs}" }));
            Report("suite", results);
        }

        var best = Best(null)?.C ?? new Config("copy", 1000, 100, 2000, 600, 10, "");
        results.Add(await RunOne(best with { Seconds = 600, Kills = 10, Label = $"chaos-{best.Path}-b{best.Batch}-f{best.FlushMs}" }));
        Report("suite", results);
    }

    static void Report(string name, List<Result> results)
    {
        var md = new StringBuilder();
        md.AppendLine(Header);
        foreach (var r in results) md.AppendLine(r.Row());
        File.WriteAllText(Path.Combine(OutDir, $"{name}-results.md"), md.ToString());
    }

    public static async Task<Result> RunOne(Config c)
    {
        Directory.CreateDirectory(OutDir);
        Log($"=== {c.Label}: {c}");
        await Docker($"exec -i {Spike.PgContainer} psql -U postgres -q -v ON_ERROR_STOP=1", File.ReadAllText("schema.sql"));
        await Docker($"restart -t 1 {Spike.MqttContainer}");
        await WaitPort(Spike.MqttPort);
        var since = DateTime.UtcNow.AddSeconds(-1);

        var latFile = Path.Combine(OutDir, $"lat-{c.Label}.bin");
        File.Delete(latFile);
        var clientId = $"fieldtrack-ingest-{c.Label}";
        var worker = await StartWorker(c, clientId, latFile);

        var startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rnd = new Random(42);
        var killAt = new Queue<double>(Enumerable.Range(0, c.Kills)
            .Select(i => (i + 0.25 + rnd.NextDouble() * 0.5) * c.Seconds / c.Kills).OrderBy(x => x));
        int kills = 0, crashes = 0;
        var sw = Stopwatch.StartNew();
        var pub = Publisher.Run(c.Rate, c.Seconds);

        while (!pub.IsCompleted)
        {
            await Task.WhenAny(pub, Task.Delay(100));
            if (worker.HasExited) { crashes++; Log($"worker exited on its own (code {worker.ExitCode}); restarting"); worker = await StartWorker(c, clientId, latFile); }
            if (killAt.Count > 0 && sw.Elapsed.TotalSeconds >= killAt.Peek())
            {
                killAt.Dequeue();
                worker.Kill(entireProcessTree: true); // TerminateProcess: no flush, no disconnect, no graceful anything
                await worker.WaitForExitAsync();
                kills++;
                Log($"killed worker #{kills} at {sw.Elapsed.TotalSeconds:F0}s");
                await Task.Delay(1000);
                worker = await StartWorker(c, clientId, latFile);
            }
        }
        var (counts, pubSecs) = await pub;
        long published = counts.Sum();
        var pubEndMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Drain: wait until every published Reading is stored, or progress stops for 30 s.
        var drain = Stopwatch.StartNew();
        long last = -1; var lastChange = Stopwatch.StartNew();
        while (true)
        {
            if (worker.HasExited) { crashes++; worker = await StartWorker(c, clientId, latFile); }
            long n;
            try { n = await Scalar("SELECT count(*) FROM reading_key"); }
            catch (Exception ex) { Log($"drain poll failed: {ex.Message}"); await Task.Delay(1000); continue; }
            if (n >= published) break;
            if (n != last) { last = n; lastChange.Restart(); }
            if (lastChange.Elapsed.TotalSeconds > 30) { Log($"drain stalled at {n}/{published}"); break; }
            await Task.Delay(200);
        }
        double drainSecs = drain.Elapsed.TotalSeconds;

        worker.StandardInput.Close();
        if (!worker.WaitForExit(15000)) worker.Kill(true);

        // Verify against the publisher's own counts: device d+1 published seq 0..counts[d]-1 in boot 1.
        await using var conn = new NpgsqlConnection(Spike.Pg);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand("CREATE TEMP TABLE expected (device_id int, n bigint)", conn)) await cmd.ExecuteNonQueryAsync();
        await using (var imp = await conn.BeginBinaryImportAsync("COPY expected FROM STDIN (FORMAT BINARY)"))
        {
            for (int d = 0; d < counts.Length; d++) { await imp.StartRowAsync(); await imp.WriteAsync(d + 1); await imp.WriteAsync(counts[d]); }
            await imp.CompleteAsync();
        }
        long stored, distinctKeys, missing, unexpected;
        await using (var cmd = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM reading),
                   (SELECT count(*) FROM (SELECT DISTINCT device_id, boot, seq FROM reading) x),
                   (SELECT coalesce(sum(e.n - coalesce(r.c, 0)), 0)::bigint FROM expected e
                      LEFT JOIN (SELECT device_id, count(DISTINCT seq) c FROM reading WHERE boot = 1 GROUP BY device_id) r USING (device_id)),
                   (SELECT count(*) FROM reading r LEFT JOIN expected e USING (device_id) WHERE e.n IS NULL OR r.seq >= e.n OR r.boot <> 1)
            """, conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            await rd.ReadAsync();
            stored = rd.GetInt64(0); distinctKeys = rd.GetInt64(1); missing = rd.GetInt64(2); unexpected = rd.GetInt64(3);
        }

        // Latency (publish -> commit) over the steady window: skip the first 10 s of publishing.
        var lats = new List<int>(); long firstCommit = long.MaxValue, lastCommit = 0;
        using (var br = new BinaryReader(File.Open(latFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            while (br.BaseStream.Length - br.BaseStream.Position >= 12)
            {
                long pubTs = br.ReadInt64(); int l = br.ReadInt32();
                firstCommit = Math.Min(firstCommit, pubTs + l); lastCommit = Math.Max(lastCommit, pubTs + l);
                if (pubTs >= startMs + 10_000 && pubTs <= pubEndMs) lats.Add(l);
            }
        lats.Sort();
        long P(double q) => lats.Count == 0 ? -1 : lats[Math.Min(lats.Count - 1, (int)(q * lats.Count))];
        double ingestRate = lastCommit > firstCommit ? stored / ((lastCommit - firstCommit) / 1000.0) : 0;

        var logs = await Docker($"logs --since {since:yyyy-MM-ddTHH:mm:ssZ} {Spike.MqttContainer}");
        int drops = logs.Split('\n').Count(l => l.Contains("dropped", StringComparison.OrdinalIgnoreCase));

        var r = new Result(c, published, published / pubSecs, stored, missing, stored - distinctKeys, unexpected,
            P(0.50), P(0.95), P(0.99), lats.Count == 0 ? -1 : lats[^1], ingestRate, drainSecs, drops, kills, crashes);
        Log($"RESULT {r.Row()}");
        File.AppendAllText(Path.Combine(OutDir, "all-runs.md"), r.Row() + "\n");
        return r;
    }

    static async Task<Process> StartWorker(Config c, string clientId, string latFile)
    {
        var exe = Environment.ProcessPath!;
        var dll = typeof(Harness).Assembly.Location;
        var workerArgs = $"worker --path {c.Path} --batch {c.Batch} --flush {c.FlushMs} --clientid {clientId} --latfile \"{latFile}\"";
        var psi = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(exe, $"\"{dll}\" {workerArgs}")
            : new ProcessStartInfo(exe, workerArgs);
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false;
        var p = Process.Start(psi)!;
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; if (e.Data.StartsWith("READY")) ready.TrySetResult(e.Data); else Log($"[worker] {e.Data}"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log($"[worker!] {e.Data}"); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var done = await Task.WhenAny(ready.Task, p.WaitForExitAsync(), Task.Delay(30000));
        if (done != ready.Task) throw new Exception("worker did not become ready");
        Log($"worker pid {p.Id} {ready.Task.Result}");
        return p;
    }

    static async Task<long> Scalar(string sql)
    {
        await using var conn = new NpgsqlConnection(Spike.Pg);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    static async Task<string> Docker(string args, string? stdin = null)
    {
        var psi = new ProcessStartInfo("docker", args)
        { RedirectStandardInput = stdin != null, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var all = await outTask + await errTask;
        if (p.ExitCode != 0) throw new Exception($"docker {args} failed: {all}");
        return all;
    }

    static async Task WaitPort(int port)
    {
        for (int i = 0; i < 100; i++)
        {
            try { using var t = new TcpClient(); await t.ConnectAsync("localhost", port); return; }
            catch { await Task.Delay(200); }
        }
        throw new Exception($"port {port} not open");
    }

    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {s}";
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(OutDir, "log.txt"), line + "\n");
    }
}
