# Results

The latest headline numbers, each linked to the JSON its run wrote. Methods and bars come from the benchmark method
([#17](https://github.com/Danie1Le/FieldTrack/issues/17)) and spec [#21](https://github.com/Danie1Le/FieldTrack/issues/21);
how to run each one is in the [README](../README.md#benchmarks). Every `bench` JSON file also records the git SHA,
Docker limits, Postgres settings and image tags of its run. The two k6 summaries hold k6 metrics only: they record no SHA
or environment, so the machine below and the commit named here are the only record of where the 2026-11-18 k6 run
happened (the earlier #126 run's commit is not recorded).

All curated runs below were taken on 2026-11-18 (UTC) from commit `8826c90`, each from an empty database, with no other
stack, test suite or benchmark running on the machine.

## Machine

| | |
|---|---|
| Host | Windows 11 Home (10.0.26300), Intel Core i7-9700F (8 cores, 8 threads, 3.0 GHz), 16 GB RAM |
| Docker | Docker Desktop, engine 29.9.2, WSL 2 VM with 8 CPUs and 7.7 GB RAM |
| Stack | `docker-compose.yml` + `bench/docker-compose.bench.yml` (`shared_buffers=1GB`, `max_wal_size=4GB`, `track_commit_timestamp=on`, `$SYS` every second); PostgreSQL 16.15 + PostGIS 3.6, Mosquitto 2.1.2 |
| Load | The simulator ran on the host, publishing to the broker through Docker Desktop's port forwarding |

These are laptop numbers: one machine runs the publishers, the broker, ingest and Postgres. No cloud runs are recorded
yet.

## Headline numbers

| Metric | Bar | Result | | Run |
|---|---|---|---|---|
| Ingestion throughput, sustained | 2,000 msg/s for 10 min, ingest p95 < 200 ms, flat backlog | 1,999.9 msg/s achieved, 1,200,019 Readings, ingest p95 112 ms, backlog flat (max 1,937), 0 missing | **Pass** | [throughput](../bench/results/throughput-20261118T015803Z.json) |
| Ingest latency (commit time minus publish) | p95 < 200 ms | p50 42 ms, p95 112 ms, p99 243 ms, max 845 ms | **Pass** | same run |
| Data loss under failure | 0 missing, 0 duplicates | 10 SIGKILLs of the API container (ingest) in 10 min at 500 msg/s: 300,004 published, 300,004 stored, 0 missing, 0 duplicates; back up in 5.4-10.0 s | **Pass** | [crash](../bench/results/crash-20261118T020937Z.json) |
| Duplicate suppression | stored = N, key conflicts = N | N = 10,000 republished byte for byte: stored 10,000, key conflicts 10,000 | **Pass** | [duplicates](../bench/results/duplicates-20261118T022031Z.json) |
| Alert latency (publish to SignalR receive) | p95 < 500 ms | 200 of 200 Alerts: p50 69 ms, p95 97 ms, p99 226 ms (server p95 69 ms, push p95 29 ms) | **Pass** | [alert-latency](../bench/results/alert-latency-20261118T022626Z.json) |
| Offline detection accuracy | 100% detected within rule + 2 s, 0 false alarms | 10 of 10 long dropouts detected in time, 0 late, 0 missed, 0 false alarms (10 short dropouts ignored) | **Pass** | [offline](../bench/results/offline-20261118T022713Z.json) |
| Alert false positives (Hysteresis) | report counts and reduction; every Fault alerts in both | 55 false Alerts with Hysteresis 0, 47 with 8 °C: 14.5% fewer; 10 of 10 Fault windows alerted in both | **Pass** | [hysteresis](../bench/results/hysteresis-20261118T023256Z.json) |
| Geofence query time | < 50 ms with the GiST index | 1% square over 1,080,000 Readings: 33.5 ms warm median with the index (cold 117 ms), 500 ms without: 14.9x | **Pass** | [geofence](../bench/results/geofence-20261118T022342Z.json) |
| History query time | report % difference | One Asset's 24 h (180 Readings): partitioned 0.55 ms vs unpartitioned 0.45 ms warm median, partitioned 22% slower (cold 27 vs 13 ms) | Reported | [history](../bench/results/history-20261118T022454Z.json) |
| API load (k6, 50 Operators, 5 min) | p95 < 150 ms, < 0.1% failed | 24,613 requests, p95 13.7 ms, 0.000% failed; per endpoint p95: Asset list 6.9 ms, Active Alerts 4.4 ms, Alert history 7.3 ms, 24 h history 19.3 ms | **Pass** | [k6](../bench/results/k6-dashboard-2026-11-18T02-45-07-062Z.json) |
| Test coverage | 80%+ lines on the Domain | 97.4% (191 of 196 coverable lines) when the gate landed, enforced on every pull request | **Pass** | CI (`build-and-test` job) |
| Infra provisioning time, cloud cost | record them | Not measured: the Azure deployment (#51) is not built yet | Open | |

The query benchmarks ran on data from `bench seed-history`
([seed run](../bench/results/seed-history-20261118T022125Z.json)): 200 Assets x 30 days x one Reading per 8 minutes along
the GPX routes, 31 daily partitions, seeded in 63 s.

## Notes and caveats

- **Sustained throughput passed here, and failed before.** The earlier 2,000 msg/s run on this laptop
  ([#44](https://github.com/Danie1Le/FieldTrack/issues/44)) failed: p50 ~97 ms but p95 6.2 s, multi-second
  `IO:WALSync` commit stalls and a backlog spiking to 24,000. That run shared the machine with other agents' test suites
  and stacks, and the simulator's pacing on Windows then fell 5% short of 2,000 msg/s (fixed in #105). On a quiet
  machine the same bar passes with margin. Treat it as a pass for this hardware with nothing else running; on a busy
  Docker Desktop the WAL flush is the first thing to give.
- **Ingest on Linux.** GitHub's Linux runners showed a different limit: a max-mode flood could drop ingest into ~200 ms
  read stalls because MQTTnet sets 8 KB socket buffers. Fixed in [#116](https://github.com/Danie1Le/FieldTrack/issues/116)
  (PR [#124](https://github.com/Danie1Le/FieldTrack/pull/124): 1 MiB on the ingest connection; 0 slow drains in 80 CI flood
  runs, against 13 before). No sustained 2,000 msg/s run on Linux is recorded.
- **Two latency clocks.** Ingest latency is the commit timestamp in Postgres (the Docker VM's clock, offset measured at
  ~1 ms) minus the Reading's Device time on the host. The same run's publish-to-PUBACK time (`tickToPubackMs`, p95 4 ms)
  is lower, although PUBACKs follow the commit, so the two clocks disagree by ~108 ms at p95 (112 ms against 4 ms). The page uses the
  stricter commit-timestamp number.
- **Crash duplicates.** The broker received 10 more PUBLISHes than were stored: publisher retries after a PUBACK
  timeout around a kill, dropped by the Reading key table as identical copies (`publisherRetryCopies`). Redeliveries are
  not counted separately until ingest metrics are exported (#51).
- **Geofence bar is shape-sensitive.** The bar applies to the headline square (5 positions). A 65-position circle of the
  same 1% took 52.4 ms warm with the index (8.8x faster than without): exact `ST_Covers` cost grows with vertices.
- **History difference is planning time.** At 180 rows both reads take under a millisecond; partition pruning costs
  more planning than it saves. A dense Asset (one Reading a second, ~86,000 rows in 24 h) is not measured.
- **Hysteresis reduction depends on the noise.** With noise sigma 2.5 °C around a 90 °C threshold, many false Alerts are
  separate excursions that no Hysteresis suppresses; Hysteresis mainly removes the flapping inside Fault windows (40 Alerts
  in 10 windows without it, 10 with it). Parameters were not tuned to flatter the result.
- **k6 ran on a young history.** The stack was fresh and the simulator (`--rate 20`, demo fleet) had published for one minute before the 5-minute test, so each Asset held at most ~1,500 Readings and the 24 h history call never had to thin. The heavy case, a full 24 h at demo rate thinned to 1,500 points, was measured when that path was built ([#126](https://github.com/Danie1Le/FieldTrack/issues/126)): p95 34 ms overall, 24 h history p95 52 ms, 0% failed ([run](../bench/results/k6-dashboard-2026-11-17T23-12-51-953Z.json)). Before #126 the history read returned every Reading and the test failed at p95 1,006 ms (#47).
- **Replay is thinned to 1,500 points.** The Asset detail replay draws at most 1,500 points per range: a 24 h route keeps
  a vertex about every 600 m, and a range of 1,500 Readings or fewer shows every Reading. At 3,000 points the readings p95
  sat on the 150 ms bar, so the cap was halved; a 24 h route is coarser as a result.
- **Known gap: From backlog tag.** The tag that marks backlogged Readings on the dashboard never shows; tracked in
  [#146](https://github.com/Danie1Le/FieldTrack/issues/146). The demo shows the backlog in History instead.
- **Coverage** is from CI, not a bench run: the Domain was at 97.4% when the gate landed (#49); CI fails a pull request below 80%.
