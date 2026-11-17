# Readings are partitioned by day, with a separate key table enforcing uniqueness

Readings are stored in a table partitioned by day on Device time, but Postgres only allows a unique constraint on a partitioned table if it includes the partition key, so (Device, Boot, Sequence number) from ADR 0001 cannot be enforced there. An unpartitioned Reading key table holds that key plus Device time and a content hash. Each batch inserts keys with `ON CONFLICT DO NOTHING` and stores only the Readings whose key was new. A matching hash on conflict counts as an identical duplicate, and a different hash as a conflicting one. The hash is over the raw payload bytes, because a Device resends a Reading byte for byte (and MQTT redelivery is byte-identical), so a copy that differs in whitespace, field order or an ignored field counts as conflicting. Key rows older than 8 days by Device time may be deleted, because Readings that late are rejected anyway. Daily partitions are created 7 days ahead by an API background job rather than pg_partman, so the same code runs on local Postgres, Azure and Neon.

## Considered Options

Measured in a spike (branch `prototype/telemetry-partitioning`, 2.16M Readings):

- **Device time in the primary key**: simple, but a re-sent Reading with a different Device time is stored twice, breaking "first copy wins".
- **Hash partitions on Device**: unique key works, but writes were about 3x slower and old data can't be dropped by date.
- **No partitioning**: simplest and fastest at demo scale, but loses dropping old data by partition and the spec's partitioning benchmark.
