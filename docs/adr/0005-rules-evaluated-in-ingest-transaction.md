# Alert Rules are evaluated inside the ingest transaction, against persisted rule state

The rules engine runs in the ingest worker, inside each batch's transaction (ADR 0004). After the batch's Readings are stored, only the newly stored Readings are evaluated. The resulting Alerts, Alert resolutions and rule state are written in the same transaction, so they commit together with the Readings or not at all. A crash before commit means the broker redelivers and the whole batch is evaluated again. A crash after commit leaves nothing undone. SignalR pushes happen after commit; a lost push only delays the dashboard, which reloads from the API.

Per-rule state (inside or outside, breached or clear, last evaluated Reading) is kept in a rule state table keyed by (Alert Rule, Asset). The worker caches it in memory, with the enabled rules, and reloads both whenever the Alert Rule version moves (Details). Alerts are raised only on a transition from clear to breached. This gives the manual-resolve latch from the alert lifecycle for free: a user who resolves an Alert while the condition holds does not change the state, so nothing new is raised until the condition clears and breaches again. A partial unique index allows at most one non-Resolved Alert per (Alert Rule, Asset).

Readings drive rules per Asset in (Boot, Sequence number) order, and only when they come after the last evaluated Reading. A different Device on the Asset counts as newer. Late stragglers are stored for history but change no rule state. Device time is not used for ordering because a device clock can jump.

## Details

- **Geofences** are checked in memory with NetTopologySuite prepared geometries. `ST_Contains` remains for history queries and the spatial-index benchmark. The first Reading for a Geofence rule only sets the baseline; an Alert needs an observed transition.
- **Threshold rules** treat unknown state as clear, so a first Reading past the threshold raises.
- **Backlogs** (Coverage gap) are evaluated Reading by Reading. A breach that started and cleared inside one batch is recorded as an Alert raised and resolved in that batch.
- **From backlog** (#146): an Alert is From backlog when its condition Cleared, by Device time, more than 10 s before the Alert's `CreatedAt`. Ingest decides it whenever it resolves an Alert as Cleared, in the same batch or a later one. Being raised and resolved in one batch is not the rule: a Device that publishes its backlog one message per PUBACK puts the raise and the clear in different batches, and a live blip shorter than one batch puts them in the same one. The 10 s margin absorbs delivery latency and a Device clock running a little behind.
- **Alert times:**
  - `RaisedAt` and platform `ResolvedAt` are the Device time of the Reading that caused them.
  - `CreatedAt` is when the platform created the Alert, used for latency measurement.
  - Offline Alerts use the sweep time.
- **Offline detection:**
  - Each batch updates `LastReceivedAt` on every Device that sent Readings.
  - The worker sweeps every 1 s, at a fixed rate, on the same loop as batch processing, so the sweep never races a batch. It reads `LastReceivedAt` from the devices row that the batches on that loop write. Before a sweep it stores every message received up to the sweep time, so a waiting Reading is never taken for silence.
  - After a worker start, no Offline Alert is raised until the broker backlog has drained and the rule's OfflineSeconds have passed. The worker knows the backlog has drained when a marker it publishes to its own session on connect comes back: Mosquitto keeps one queue per session, so the marker arrives behind every message queued before it.
- **Alert Rule version:** a one-row counter that every change to what the worker evaluates or caches bumps in its own transaction: Alert Rule and Geofence changes, and the Asset Archives, Device Archives and detaches that resolve Active Alerts or reset rule state. Each batch reads it `FOR SHARE` after its Device and Asset locks and, when it moved, reloads the enabled rules and all rule state in the same transaction. A sweep reloads when it moved before judging silence; one that raises reads it `FOR SHARE` again after locking the Assets and raises nothing if it moved meanwhile. A change and a batch therefore never interleave: the change waits for a running batch and sees its Alerts, or the batch waits for the change and sees all of it.
- **Lock order**, on every path that takes more than one: Device, then Asset (`FOR KEY SHARE` or stronger), then the Alert Rule version, then the Asset Group, then Alert Rules, Alerts and rule state. An Asset Group delete bumps the version before it locks the group `FOR UPDATE`, and a rule change takes the group through its foreign key check after its bump. A rule change locks its target Asset before it checks that the Asset is not Archived and before it bumps the version, so the rule's foreign key check never takes the Asset after the version.
- **Manual against platform resolve:** both use a conditional update (only when not yet Resolved); the first wins and the other is a no-op.

## Considered options

- **Evaluate after commit, in a separate step:** keeps the ingest transaction short, but a crash between the two steps loses evaluations unless an outbox replays them. Redelivered Readings are dropped as duplicates, so they would never be evaluated again.
- **`ST_Contains` in the database per batch:** matches the spec's wording, but adds a spatial query to every batch, when the handful of Geofences fits in memory.
- **Rebuild state at startup from Active Alerts and the latest Readings:** no extra table, but loses the latch and the geofence baseline across restarts.
- **Order by Device time:** natural for history, but a clock jump would skip or replay evaluations.
- **From backlog when one batch raises and resolves** (the first rule): depends on batching, so it never fired for a backlog sent one Reading per PUBACK, and it tagged live blips. Making the simulator pipeline its backlog would only move the accident to the 25 ms batch window.
- **Postgres LISTEN/NOTIFY for rule changes:** faster to react, but needs a dedicated connection and a fallback for missed notices.

## Consequences

- The ingest worker must run as a single instance. In-memory caches and transition-based raising assume one evaluator.
- Rule evaluation time adds to every batch's commit time, and so to the 200 ms ingest and 500 ms alert latency budgets.
- A change that bumps the Alert Rule version holds every ingest batch until it commits, which also counts against the 200 ms budget. Such transactions stay short and bump the version after any slow read (Asset Archive checks Reading history first).
- A Reading that arrives after a newer one from the same Device never raises or resolves an Alert.
- Only the last evaluated Reading is kept, so after a Device swap an old Device's Coverage-gap backlog that arrives later counts as newer and is evaluated. Within one batch the Device evaluated last goes first, so its own stragglers are still skipped before another Device's Readings move the state on.
- The rules engine has two entry points, one per trigger, where spec #21 describes one: `RuleEvaluator.Evaluate` for a batch's Readings (every rule kind) and `RuleEvaluator.Sweep` for the sweep tick (Offline rules only). Each keeps its input plain; both return the same new rule state plus Alerts to raise and resolve.
- An Offline rule clears only on a Reading from the Device attached to the Asset now. After a Device swap, the old Device's late backlog is still stored, but it does not resolve the Offline Alert of a Device that is still silent.
