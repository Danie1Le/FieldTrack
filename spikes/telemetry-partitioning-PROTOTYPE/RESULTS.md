# PROTOTYPE results: telemetry partitioning vs idempotent writes

Throwaway spike for the Wayfinder ticket *Telemetry table: time partitions vs idempotent writes*. Never merged.

**Question:** how can Readings be partitioned by time while each Reading, identified by (Device, Boot, Sequence number) per ADR 0001, is stored only once? Postgres requires a unique constraint on a partitioned table to include the partition key.

**Setup:** `postgis/postgis:18-3.6` (PostgreSQL 18.6) in Docker Desktop, 8 CPU / 8 GB VM. 50 Devices, one Reading per minute for 30 days (2.16M rows), then 200 batches of 1,000 fresh Readings, then the same 200k replayed, then 1,000 conflicting duplicates (same key, Device time +1 s). Timings are server-side only (no client, network or MQTT), so compare the columns, not the absolute numbers. Run with `bash spikes/telemetry-partitioning-PROTOTYPE/run.sh`.

| test | a: range on Device time, PK incl. time | b: range on Device time + key table | c: hash on Device | d: no partitioning |
|---|---|---|---|---|
| bulk load 2.16M rows (s) | 144.4 | 97.2 | 90.6 | 76.7 |
| fresh upsert (rows/s) | 22,164 | 23,072 | 7,839 | 16,418 |
| identical replay: rows wrongly stored | 0 | 0 | 0 | 0 |
| identical replay (rows/s) | 137,253 | 211,115 | 160,429 | 218,296 |
| conflicting duplicates wrongly stored (of 1,000) | **1,000** | 0 | 0 | 0 |
| 24 h route query, median of 7 (ms) | 3.58 | 1.18 | 0.57 | 0.30 |
| size incl. indexes (MB) | 600 | 692 | 542 | 549 |

Runtime partition pruning confirmed for (a)/(b): the route query executes against one daily partition.

**Verdict:** (b). Option (a) cannot honour "first copy wins" because its key must include Device time. (c) has the slowest writes and no time-based retention. (d) is simplest and fastest at this size but drops time partitioning. (b) keeps daily partitions, rejects every duplicate, and writes as fast as any option.
