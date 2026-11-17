# Ingest writes Readings with binary COPY and acknowledges MQTT messages only after commit

The ingest worker turns off MQTTnet's automatic acknowledgement. It collects messages into batches of up to 1,000 Readings, closing a batch 25 ms after its first message. Each batch is written in one transaction: Npgsql binary COPY into a temporary stage table, then a single statement inserts the keys into the Reading key table with `ON CONFLICT DO NOTHING` (ADR 0003) and inserts the Readings whose keys were new. Only after the commit does the worker send PUBACKs, in the order the messages arrived. A crash before commit therefore leaves the messages unacknowledged, the broker redelivers them, and the key table drops any copy already stored. The worker keeps a fixed ClientId and a persistent session (`CleanStart = false`, session expiry 3,600 s, `ReceiveMaximum` twice the batch size). Mosquitto runs with persistence on and `max_queued_messages 0`, so the broker never drops queued messages for the worker. EF Core remains the data access for everything except this write path.

## Considered Options

Measured in a spike (branch `prototype/ingest-spike`, see `spikes/ingest-PROTOTYPE/RESULTS.md`): 200 devices at 2,000 msg/s, Docker Desktop.

- **EF Core for ingest** (look up existing keys, skip them, `AddRange` + `SaveChanges`): met the latency bar in short runs and lost nothing across 10 hard crashes. However, in a 10-minute crash run each batch slowed from about 30 ms to 0.4–2.7 s once the key table passed about 800k rows, because the separate key lookup missed the buffer cache. COPY used the same index through `ON CONFLICT` and stayed at 50–68 ms per batch.
- **Acknowledge on receipt** (MQTTnet default): simpler, but a crash between receipt and commit loses the Readings in memory.
- **Longer flush intervals** (50–250 ms): fewer commits, but p95 publish-to-commit latency rose to 226–505 ms against a 200 ms target. A 25 ms flush gave a p95 of 184 ms on COPY.
- **Capped broker queue**: bounds broker memory, but Mosquitto drops messages past the cap while the worker is down, breaking zero loss.

## Consequences

- The ingest write is hand-written SQL, outside EF Core migrations' view; schema changes to the Reading, Reading key or Rejected message tables must update it too.
- Rejected messages have no key a resend repeats, so a crash between commit and PUBACK would store them twice. Ingest drops a redelivered message (MQTT DUP set) whose topic and payload were rejected in the last hour, the broker session's expiry, so stored plus rejected still equals published. A Device that publishes the same rejected message again, without DUP, is counted again.
- Broker memory grows with any worker outage, so Mosquitto's container needs headroom for the longest expected outage.
- Postgres needs enough memory to keep the Reading key primary-key index cached; the spike's slowdown came from cache misses on 128 MB `shared_buffers`.
