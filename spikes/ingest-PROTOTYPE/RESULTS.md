# Ingest spike results (PROTOTYPE, throwaway)

Ticket: [Ingest spike](https://github.com/Danie1Le/FieldTrack/issues/15). This branch is never merged.

## Question

Can one ingest worker take MQTT telemetry from Mosquitto into PostgreSQL + PostGIS with zero lost and zero duplicate Readings, survive hard crashes, and hold a 200 ms p95 from publish to commit at the expected rate? Should the write path be EF Core or Npgsql binary COPY, and with which batch and flush settings?

## Setup

- Windows 11, Docker Desktop. `postgis/postgis:18-3.6` (default config, `shared_buffers` 128 MB) and `eclipse-mosquitto:2.0.22`, each in a container. Worker and publisher run on the host (.NET 10, MQTTnet 5.2).
- 200 simulated devices, QoS 1, 2,000 msg/s total unless noted.
- Worker: `AutoAcknowledge = false`, messages go into a Channel, batches close on size or flush interval, one DB transaction per batch, then PUBACKs are sent in receive order. Fixed ClientId, `CleanStart = false`, session expiry 3,600 s, `ReceiveMaximum = 2 × batch`.
- Mosquitto: `max_queued_messages 0` (unlimited), `max_inflight_messages 100`.
- Schema follows ADR 0003: an unpartitioned `reading_key` table (PK device_id, boot, seq) plus daily-partitioned `reading`. Duplicates are dropped with `ON CONFLICT DO NOTHING` on `reading_key`.
- COPY path: binary COPY into a temp stage table, then one statement inserts into `reading_key` and joins the inserted keys into `reading`.
- EF Core path: query existing keys for the batch, skip them, then `AddRange` + `SaveChanges`.
- Latency is publisher timestamp to commit timestamp, excluding the first 10 s of each run.
- Crash method: `Process.Kill(entireProcessTree: true)` on the worker, restart after 1 s.

## Results: 3-minute runs, 2,000 msg/s, no crashes

Every run: zero missing, zero duplicates, zero broker drops, drain under 0.5 s.

| Path | Batch | Flush ms | p50 ms | p95 ms | p99 ms |
|---|---|---|---|---|---|
| COPY | 250 | 100 | 146 | 175 | 218 |
| COPY | 500 | 100 | 197 | 280 | 319 |
| COPY | 1000 | 100 | 193 | 273 | 292 |
| COPY | 2000 | 100 | 192 | 273 | 292 |
| COPY | 1000 | 50 | 172 | 226 | 271 |
| COPY | 1000 | 25 | 132 | 184 | 200 |
| COPY | 1000 | 250 | 223 | 373 | 410 |
| EF Core | 250 | 100 | 161 | 213 | 245 |
| EF Core | 500 | 100 | 176 | 260 | 347 |
| EF Core | 1000 | 100 | 179 | 277 | 426 |
| EF Core | 2000 | 100 | 136 | 215 | 281 |
| EF Core | 1000 | 50 | 126 | 162 | 210 |
| EF Core | 1000 | 25 | 103 | 141 | 540 |
| EF Core | 1000 | 250 | 341 | 505 | 538 |

At 2,000 msg/s, batches rarely fill, so the flush interval sets latency. Batch size barely matters. Only a 25 ms flush clears the 200 ms p95 bar on both paths.

## Results: flood (60 s, publisher unthrottled)

| Path | Batch | Flush ms | Published | Publish rate | Ingest rate | Lost | Duplicates |
|---|---|---|---|---|---|---|---|
| COPY | 250 | 100 | 1,999,615 | 33,300/s | 4,061/s | 0 | 0 |
| EF Core | 1000 | 25 | 1,852,995 | 30,860/s | 7,374/s | 0 | 0 |

The broker queued the backlog without dropping anything. The two runs used different batch sizes, so they are not a like-for-like comparison of the paths.

## Results: 10-minute chaos runs, batch 1000, flush 25 ms, 10 hard kills

| Path | Stored = broker received | Duplicates | Sequence gaps | Batch write time | p50 ms | p95 ms | p99 ms |
|---|---|---|---|---|---|---|---|
| COPY | 1,141,484 | 0 | 0 | 50–68 ms throughout | 141 | 766 | 2,278 |
| EF Core | 1,101,184 | 0 | 0 | ~30 ms rising to 0.4–2.7 s after ~800k rows | 130 | 63,193 | 122,116 |

- Both paths lost nothing across 10 crashes. The stored count equals the broker's own `$SYS/broker/publish/messages/received` counter, and every device's Sequence numbers are contiguous. Redeliveries after each crash were absorbed by `ON CONFLICT DO NOTHING`.
- COPY's p95 and p99 come from the backlog after each crash (worker down about 1–2 s). Between crashes it matched the no-crash runs.
- EF Core slowed down sharply once `reading_key` passed about 800k rows. The key-lookup query plan was right (index nested loop), but its index pages missed the 128 MB buffer cache and each read cost about 2.5 ms from disk inside Docker Desktop. COPY uses the same index through `ON CONFLICT` and did not slow down, so the cost is EF Core's separate lookup round trip.

## Harness problems (not ingest problems)

- The EF Core chaos harness crashed while polling the drain (`Npgsql.NpgsqlException: Exception while reading from stream`). The poll now tolerates errors. Zero loss was confirmed by hand.
- In the COPY chaos run, 6 of the 20 publisher connections closed from the client side about 90 s before the end. The publish retry loop never reconnects, so 60 devices stopped early and the harness hung until it was killed. Correctness is unaffected; the cause of the drop was not investigated.

## Deviations from the agreed design

- The spike payload is snake_case with `ts` as an ISO string. The agreed contract is camelCase with `ts` in epoch ms. Parsing cost is not material at these rates.
- Everything ran on Docker Desktop on one laptop. Absolute numbers will differ in Azure; the comparisons should hold.

## Recommendation

- Write path: Npgsql binary COPY into a stage table, then one `INSERT ... ON CONFLICT DO NOTHING` into `reading_key` joined into `reading`. Use EF Core for everything else.
- Batch up to 1,000 Readings, flush 25 ms after the first message.
- Keep the worker settings above: manual acknowledgement after commit, in receive order; fixed ClientId; persistent session; `ReceiveMaximum = 2 × batch`.
- Mosquitto: `max_queued_messages 0` and persistence on in the real deployment.
- Size Postgres memory so the `reading_key` primary-key index stays in cache.
