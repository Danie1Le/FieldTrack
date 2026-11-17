# Alert Rules are evaluated inside the ingest transaction, against persisted rule state

The rules engine runs in the ingest worker, inside each batch's transaction (ADR 0004). After the batch's Readings are stored, only the newly stored Readings are evaluated. The resulting Alerts, Alert resolutions and rule state are written in the same transaction, so they commit together with the Readings or not at all. A crash before commit means the broker redelivers and the whole batch is evaluated again. A crash after commit leaves nothing undone. SignalR pushes happen after commit; a lost push only delays the dashboard, which reloads from the API.

Per-rule state (inside or outside, breached or clear, last evaluated Reading) is kept in a rule state table keyed by (Alert Rule, Asset). The worker caches it in memory and loads it at startup. Alerts are raised only on a transition from clear to breached. This gives the manual-resolve latch from the alert lifecycle for free: a user who resolves an Alert while the condition holds does not change the state, so nothing new is raised until the condition clears and breaches again. A partial unique index allows at most one non-Resolved Alert per (Alert Rule, Asset).

Readings drive rules per Asset in (Boot, Sequence number) order, and only when they come after the last evaluated Reading. A different Device on the Asset counts as newer. Late stragglers are stored for history but change no rule state. Device time is not used for ordering because a device clock can jump.

## Details

- **Geofences** are checked in memory with NetTopologySuite prepared geometries. `ST_Contains` remains for history queries and the spatial-index benchmark. The first Reading for a Geofence rule only sets the baseline; an Alert needs an observed transition.
- **Threshold rules** treat unknown state as clear, so a first Reading past the threshold raises.
- **Backlogs** (Coverage gap) are evaluated Reading by Reading. A breach that started and cleared inside the backlog is recorded as an Alert raised and resolved in the same batch.
- **Alert times:**
  - `RaisedAt` and platform `ResolvedAt` are the Device time of the Reading that caused them.
  - `CreatedAt` is when the platform created the Alert, used for latency measurement.
  - Offline Alerts use the sweep time.
- **Offline detection:**
  - Each batch updates `LastReceivedAt` on every Device that sent Readings.
  - The worker sweeps every 1 s, at a fixed rate, on the same loop as batch processing, so the sweep never races a batch. It reads `LastReceivedAt` from the devices row that the batches on that loop write. Before a sweep it stores every message received up to the sweep time, so a waiting Reading is never taken for silence.
  - After a worker start, no Offline Alert is raised until the broker backlog has drained and the rule's OfflineSeconds have passed. The worker knows the backlog has drained when a marker it publishes to its own session on connect comes back: Mosquitto keeps one queue per session, so the marker arrives behind every message queued before it.
- **Rule and Geofence edits** bump a version row in the edit's transaction. The worker checks the version before each batch and reloads when it moved.
- **Manual against platform resolve:** both use a conditional update (only when not yet Resolved); the first wins and the other is a no-op.

## Considered Options

- **Evaluate after commit, in a separate step:** keeps the ingest transaction short, but a crash between the two steps loses evaluations unless an outbox replays them. Redelivered Readings are dropped as duplicates, so they would never be evaluated again.
- **`ST_Contains` in the database per batch:** matches the spec's wording, but adds a spatial query to every batch, when the handful of Geofences fits in memory.
- **Rebuild state at startup from Active Alerts and the latest Readings:** no extra table, but loses the latch and the geofence baseline across restarts.
- **Order by Device time:** natural for history, but a clock jump would skip or replay evaluations.
- **Postgres LISTEN/NOTIFY for rule changes:** faster to react, but needs a dedicated connection and a fallback for missed notices.

## Consequences

- The ingest worker must run as a single instance. In-memory caches and transition-based raising assume one evaluator.
- Rule evaluation time adds to every batch's commit time, and so to the 200 ms ingest and 500 ms alert latency budgets.
- A Reading that arrives after a newer one from the same Device never raises or resolves an Alert.
- Only the last evaluated Reading is kept, so after a Device swap an old Device's Coverage-gap backlog that arrives later counts as newer and is evaluated. Within one batch the Device evaluated last goes first, so its own stragglers are still skipped before another Device's Readings move the state on.
- The rules engine has two entry points, one per trigger, where spec #21 describes one: `RuleEvaluator.Evaluate` for a batch's Readings (every rule kind) and `RuleEvaluator.Sweep` for the sweep tick (Offline rules only). Each keeps its input plain; both return the same new rule state plus Alerts to raise and resolve.
- An Offline rule clears only on a Reading from the Device attached to the Asset now. After a Device swap, the old Device's late backlog is still stored, but it does not resolve the Offline Alert of a Device that is still silent.
