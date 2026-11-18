# FieldTrack: Real-Time Equipment & Fleet Monitoring Platform

> This is the original proposal. The current spec is [issue #21](https://github.com/Danie1Le/FieldTrack/issues/21); where they differ, #21 wins. Measured results are on [RESULTS.md](RESULTS.md).

## 1. Overview

FieldTrack tracks field equipment and vehicles (trucks, excavators, generators, substation sensors) in real time. Simulated devices publish GPS position and sensor readings over MQTT. The platform ingests them, stores them in a spatial database, evaluates alert rules (geofence exit, threshold breach, device offline), and pushes everything live to a map dashboard.

It mirrors what stable industrial and engineering-software companies build: Trimble (fleet and construction tracking), Siemens and Itron (industrial and utility IoT), SEL and utilities (grid devices), Esri and Hexagon (spatial data), Daimler Truck and PACCAR (vehicle telematics), and Tektronix, Keysight and Fluke (test and measurement data).

**Resume gaps this project closes:** a full ASP.NET Core application, EF Core, deep SQL with spatial queries, MQTT/IoT ingestion, PostGIS and maps, Angular, SignalR real-time, Azure hosting, Terraform on Azure, JWT auth and roles, xUnit + Testcontainers, OpenTelemetry.

---

## 2. Requirements

### 2.1 Functional requirements

| ID | Requirement |
|---|---|
| FR-1 | Simulated devices publish telemetry (GPS, engine temperature, fuel level, vibration, battery voltage) over MQTT at a configurable rate. |
| FR-2 | The platform ingests every message, validates it, and stores it with its device timestamp and received timestamp. |
| FR-3 | Duplicate messages (same device + sequence number) are stored only once. |
| FR-4 | Users can create, edit, and delete assets and assign each asset a device. |
| FR-5 | Users can draw geofences (polygons) on the map and attach them to assets or asset groups. |
| FR-6 | The rules engine raises alerts for: geofence exit/enter, threshold breach (e.g. engine temp > 105 C), and device offline (no message for N seconds). |
| FR-7 | Alerts are pushed to connected browsers in real time and can be acknowledged and resolved. |
| FR-8 | The dashboard shows a live map of all assets, their latest status, and an alert feed. |
| FR-9 | Users can view an asset's history: route replay on the map and sensor charts over a chosen time range. |
| FR-10 | Users can export an asset's telemetry for a time range as CSV. |
| FR-11 | Users log in; roles are Admin (manage assets, geofences, rules, users) and Operator (view, acknowledge alerts). |
| FR-12 | All API endpoints are documented with OpenAPI (Swagger). |

### 2.2 Non-functional requirements

| ID | Requirement | Target |
|---|---|---|
| NFR-1 | Ingestion throughput | Sustain at least 2,000 messages/s on a single API container |
| NFR-2 | Alert latency (reading published to alert shown in browser) | p95 under 500 ms |
| NFR-3 | Durability | Zero lost readings across forced restarts of the ingest worker (MQTT QoS 1 + idempotent writes) |
| NFR-4 | Geofence query speed | Point-in-polygon check on 1M stored points under 50 ms with a spatial index |
| NFR-5 | Test coverage | 80%+ line coverage on domain and rules logic |
| NFR-6 | Reproducible infrastructure | Entire Azure environment created and destroyed with Terraform |
| NFR-7 | Observability | Traces, metrics, and logs for every request and ingested message |
| NFR-8 | Security | JWT auth, role-based authorization, secrets in Azure Key Vault, no secrets in the repo |

---

## 3. Features

1. **Device simulator.** A C# console app that spins up N virtual devices driving along routes (GPX files or random walks), with configurable message rate, sensor noise, injected faults (overheat, fuel leak), and dropouts (to test offline detection).
2. **MQTT ingestion.** Mosquitto broker; an ASP.NET Core `BackgroundService` subscribes with MQTTnet, validates payloads, batches inserts, and acknowledges only after a durable write.
3. **Idempotent storage.** A unique constraint on (device_id, sequence_no) plus upserts, so duplicate deliveries never create duplicate rows.
4. **Spatial database.** PostgreSQL + PostGIS for positions and geofence polygons, with GiST indexes; telemetry table partitioned by time.
5. **Rules engine.** Evaluates each reading against active rules: geofence enter/exit (`ST_Contains`), sensor thresholds with hysteresis to prevent alert flapping, and offline detection via a timer sweep.
6. **Real-time push.** SignalR hub that broadcasts position updates and new alerts to subscribed dashboard clients.
7. **REST API.** ASP.NET Core Web API for assets, devices, geofences, rules, alerts, history, and CSV export; OpenAPI docs.
8. **Angular dashboard.** Leaflet map with live markers, geofence drawing tool, alert feed with acknowledge/resolve, asset detail page with route replay and sensor charts.
9. **Auth.** ASP.NET Core Identity issuing JWTs (or Microsoft Entra ID), with Admin and Operator roles.
10. **Infrastructure as code.** Terraform (azurerm provider) provisions everything on Azure; remote state in an Azure Storage account.
11. **CI/CD.** GitHub Actions: build, test, container image push to Azure Container Registry, `terraform plan` on PRs, `terraform apply` and deploy on main.
12. **Observability.** OpenTelemetry instrumentation exported to Azure Monitor / Application Insights (or Prometheus + Grafana locally), with a dashboard for ingest rate, alert latency, and error rate.
13. **Load and chaos testing.** k6 scripts for API load; a simulator "flood" mode for ingestion load; a script that kills the ingest container mid-run to prove zero data loss.

---

## 4. Core Entities

| Entity | Key fields | Notes |
|---|---|---|
| **Asset** | Id, Name, Type (Truck, Excavator, Generator, Sensor), GroupId, Status, CreatedAt | A tracked piece of equipment |
| **AssetGroup** | Id, Name | e.g. "Portland Site A" |
| **Device** | Id, SerialNumber, AssetId, FirmwareVersion, LastSeenAt | Physical/simulated transmitter attached to an asset |
| **TelemetryReading** | Id, DeviceId, SequenceNo, DeviceTimestamp, ReceivedAt, Location (PostGIS `geography(Point)`), SpeedKph, EngineTempC, FuelPct, Vibration, BatteryV | Partitioned by time; unique (DeviceId, SequenceNo) |
| **Geofence** | Id, Name, Area (PostGIS `geography(Polygon)`), Type (Inclusion/Exclusion) | GiST index on Area |
| **AlertRule** | Id, Name, Kind (GeofenceExit, GeofenceEnter, Threshold, Offline), TargetAssetId or GroupId, GeofenceId?, Metric?, Operator?, Threshold?, Hysteresis?, OfflineSeconds?, Severity, Enabled | Configurable by Admins |
| **Alert** | Id, RuleId, AssetId, ReadingId?, Severity, Message, Status (Open, Acknowledged, Resolved), RaisedAt, AcknowledgedBy?, ResolvedAt? | Pushed via SignalR |
| **User** | Id, Email, Role (Admin, Operator) | ASP.NET Core Identity |

Relationships: AssetGroup 1-N Asset; Asset 1-1 Device; Device 1-N TelemetryReading; Asset/AssetGroup 1-N AlertRule; AlertRule 1-N Alert; Geofence 1-N AlertRule.

---

## 5. Architecture

```
                         +------------------------+
                         |  Device Simulator (C#) |
                         |  N virtual devices     |
                         +-----------+------------+
                                     | MQTT (QoS 1)
                                     v
                         +------------------------+
                         |   Mosquitto broker     |
                         +-----------+------------+
                                     |
                                     v
+------------------------------------------------------------------+
|                 ASP.NET Core (FieldTrack.Api)                    |
|                                                                  |
|  IngestWorker (BackgroundService, MQTTnet)                       |
|     -> validate -> batch upsert -> RulesEngine -> AlertService   |
|                                                   |              |
|  REST controllers (assets, geofences, rules,      |  SignalR Hub |
|  alerts, history, export)  <-- JWT auth --        +-----> clients|
|                                                                  |
|  OfflineDetector (timer sweep)                                   |
+-------------------------------+----------------------------------+
                                |
                                v
                 +-------------------------------+
                 | PostgreSQL 16 + PostGIS       |
                 | partitioned telemetry, GiST   |
                 +-------------------------------+

Angular + Leaflet dashboard  <--- REST + SignalR --->  FieldTrack.Api

Observability: OpenTelemetry -> Azure Monitor / App Insights (Grafana locally)
```

### Azure deployment (all via Terraform)

| Component | Azure service |
|---|---|
| API + ingest worker | Azure Container Apps, Consumption profile, exactly 1 replica while deployed (the ingest worker is a single instance, so SignalR needs no backplane) |
| Device simulator | A second Container App at demo rate |
| MQTT broker | Mosquitto everywhere: a container locally, a Container App reached over MQTT over WebSockets (`wss://…:443`) in Azure (spec #21, ADR 0006) |
| Database | Azure Database for PostgreSQL Flexible Server, Burstable B1ms (PostgreSQL 16, PostGIS extension enabled), only while demoing |
| Angular dashboard | The nginx dashboard container as a Container App, proxying `/api` and `/hubs` to the API (one HTTPS address, no CORS) |
| Images | GitHub Container Registry (free for public repos) instead of Azure Container Registry |
| Secrets | Azure Key Vault (managed identity access) |
| Monitoring | Application Insights + Log Analytics (stay under the free monthly data allowance) |
| Terraform state | Azure Storage account backend |

The demo runs on the developer's Azure free account and is created and destroyed per session (ADR 0006). See section 11 for the $0 budget rules.

### Repository layout

```
fieldtrack/
  src/
    FieldTrack.Api/            ASP.NET Core API, SignalR hub, workers
    FieldTrack.Domain/         entities, rules engine (no infrastructure deps)
    FieldTrack.Infrastructure/ EF Core DbContext, migrations, MQTT client
    FieldTrack.Simulator/      device simulator console app
  web/                         Angular dashboard
  tests/
    FieldTrack.UnitTests/      xUnit: rules engine, hysteresis, validation
    FieldTrack.IntegrationTests/ xUnit + Testcontainers (PostGIS, Mosquitto)
  load/                        k6 scripts, chaos scripts
  infra/terraform/             azurerm modules + environments
  docker-compose.yml           local stack: postgres/postgis, mosquitto, api, web, grafana
  .github/workflows/           CI/CD
```

---

## 6. Tech Stack

| Layer | Technology |
|---|---|
| Language | C# / .NET 10 (LTS), TypeScript (spec #21) |
| Backend | ASP.NET Core Web API, BackgroundService, SignalR |
| Data access | Entity Framework Core 10 + Npgsql + NetTopologySuite (spatial types); binary COPY for ingest (ADR 0004) |
| Database | PostgreSQL 16 + PostGIS |
| Messaging | MQTT 3.1.1/5 with Eclipse Mosquitto, MQTTnet client |
| Frontend | Angular 22, Leaflet 1.9 (+ Leaflet-Geoman for Geofences), Chart.js (spec #21) |
| Auth | ASP.NET Core Identity + JWT bearer, role-based policies |
| Testing | xUnit v3, Shouldly, Testcontainers for .NET, Vitest, Playwright, k6 (spec #21) |
| Observability | OpenTelemetry .NET, Application Insights / Prometheus + Grafana |
| Containers | Docker, Docker Compose |
| Cloud | Microsoft Azure |
| IaC | Terraform (azurerm provider), remote state in Azure Storage |
| CI/CD | GitHub Actions (build, test, image push to GitHub Container Registry, terraform plan/apply), free for public repos |

---

## 7. Outputs

What the finished project produces and what a reviewer or interviewer can see:

1. **Live dashboard.** A deployed Angular web app with a map of moving assets, color-coded by status, geofences drawn as polygons, and an alert feed updating in real time.
2. **Asset detail view.** Route replay on the map plus time-series charts of engine temperature, fuel, and vibration for any time range.
3. **Alert workflow.** Alerts appear within a fraction of a second, can be acknowledged and resolved, and keep an audit trail (who, when).
4. **REST API with Swagger docs.** Public OpenAPI page documenting every endpoint.
5. **CSV export** of telemetry for any asset and time range.
6. **Monitoring dashboard** (App Insights or Grafana) showing ingest rate, alert latency, error rate, and database query time.
7. **Benchmark report** (`docs/RESULTS.md`) with the measured numbers from section 8, the hardware/Azure SKU used, and how to reproduce each test.
8. **One-command infra.** `terraform apply` builds the full Azure environment; `terraform destroy` tears it down.
9. **README** with an architecture diagram, demo GIF or video, local setup (`docker compose up`), and links to the live demo and API docs.

---

## 8. What It Can Measure

Measure each of these with a repeatable script, record the result in `docs/RESULTS.md`, and use the real numbers in resume bullets.

| Metric | How to measure | Target |
|---|---|---|
| **Ingestion throughput** (messages/s) | Simulator flood mode with N devices; read OpenTelemetry ingest counter | 2,000+ msg/s sustained |
| **Ingest latency** (publish to DB commit) | Compare device timestamp to commit time; p50 / p95 / p99 | p95 < 200 ms |
| **Alert latency** (publish to browser) | Stamp reading on publish, record time the SignalR client receives the alert | p95 < 500 ms |
| **Data loss under failure** | Kill the ingest container N times mid-run; compare published vs stored counts | 0 lost, 0 duplicated |
| **Duplicate suppression** | Replay the same message batch twice; count rows | Duplicates = 0 |
| **Geofence query time** | Point-in-polygon over 1M readings, with vs without GiST index (`EXPLAIN ANALYZE`) | X% faster with index; < 50 ms |
| **History query time** | 24-hour route for one asset, with vs without time partitioning | X% faster with partitioning |
| **API load** | k6: concurrent users hitting dashboard endpoints; p95 latency and error rate | p95 < 150 ms, < 0.1% errors |
| **Offline detection accuracy** | Simulator drops devices; check alerts fire within the configured window | 100% detected, no false alarms |
| **Alert false positives** | Noisy sensor data near thresholds, with vs without hysteresis | X% fewer false alerts with hysteresis |
| **Test coverage** | Coverlet report from xUnit | 80%+ on domain + rules |
| **Infra provisioning time** | Time `terraform apply` from empty subscription to running app | Record it |
| **Cloud cost** | Azure Cost Management per demo session and per idle day; a week of running would drain the free grant (spec #21) | Record $/demo session and $/idle day |

---

## 9. Milestones

| Milestone | Deliverables |
|---|---|
| 1 | Domain model, EF Core + PostGIS migrations, simulator, Mosquitto, ingest worker with idempotent upserts, REST API + Swagger, docker-compose |
| 2 | Rules engine (geofence, threshold with hysteresis, offline), SignalR hub, Angular dashboard with live map, geofence drawing, alert feed, asset history |
| 3 | JWT auth + roles, xUnit + Testcontainers tests, k6 + chaos scripts, OpenTelemetry, Terraform for Azure, GitHub Actions CI/CD, measurements + RESULTS.md, README + demo |

---

## 10. Resume Bullet Templates

Fill these in only with your own measured numbers once the project is built and measured:

- Built an equipment-tracking platform in **ASP.NET Core** that ingests X MQTT readings/s from simulated assets with Y ms p95 alert latency
- Cut geofence lookup time X% on 1M GPS points by adding a **PostGIS** GiST spatial index
- Lost zero of X readings across Y forced crashes using MQTT QoS 1 and idempotent **EF Core** upserts
- Pushed real-time alerts to an **Angular** + Leaflet map via SignalR, deployed to **Azure** with **Terraform**
- Reduced false alerts X% on noisy sensor data by adding hysteresis to threshold rules

---

## 11. Budget: $0

Hard rule: the project must never cost real money. Everything is built and benchmarked locally for free, and Azure is used only through the free account's $200 credit (valid 2026-11-18 to 2026-12-18), never upgraded, so it cannot charge the card.

### 11.1 Free by default (local development)

All of these are open source or free: .NET 10, ASP.NET Core, EF Core, PostgreSQL + PostGIS, Eclipse Mosquitto, MQTTnet, Angular, Leaflet, Grafana, Prometheus, k6, xUnit, Testcontainers, Docker Desktop (free for personal/education use), Terraform. GitHub, GitHub Actions, and GitHub Container Registry are free for public repositories.

The full system runs locally with `docker compose up` (Postgres/PostGIS, Mosquitto, API, Angular, Grafana). All benchmarks in section 8 are run locally, so the numbers never depend on paid cloud capacity.

### 11.2 Azure without spending money

1. **Use the Azure free account** ($200 credit for 30 days, 2026-11-18 to 2026-12-18, in the developer's own billing account). Azure for Students could not verify the school account (#8). The card on file is for identity only, and nothing is charged unless the account is upgraded. When the credit runs out or expires, services stop and the cloud demo is over (ADR 0006).
2. **Never upgrade to Pay-As-You-Go.** Check the account plan (free) and the remaining credit before every apply.
3. **Set budget alerts** (Cost Management > Budgets): an email at $1 of usage and another near the end of the credit, so any unexpected usage is visible right away. The budget lives in the `bootstrap` Terraform stack.
4. **Deploy only to demo.** Run the `demo` stack's apply from GitHub Actions before a demo or recording, then destroy it right after. Only the `bootstrap` stack (state, identity, budget) stays.
5. **Pick free tiers in Terraform:**

| Component | $0 choice |
|---|---|
| Angular dashboard | nginx dashboard Container App, Consumption profile (no Static Web Apps) |
| API + ingest worker | Azure Container Apps consumption plan, the API with the ingest worker at exactly 1 replica while deployed (the worker must stay connected to the broker), covered by the credit at demo traffic |
| MQTT broker | Mosquitto as a Container App on the Consumption profile, reached over MQTT over WebSockets with password auth (spec #21, ADR 0006; replaces the HiveMQ Cloud plan) |
| Database | Azure PostgreSQL Flexible Server Burstable B1ms (PostgreSQL 16 with PostGIS), only while deployed (covered by the free account's 12-month hours); destroyed after each demo. Free fallback: a free-tier hosted Postgres that supports PostGIS (Neon or Supabase) |
| Container images | GitHub Container Registry (free), not Azure Container Registry |
| Monitoring | Application Insights through the Azure Monitor OpenTelemetry Distro, with sampling and a daily cap on; Grafana locally for benchmarks |
| Secrets | Azure Key Vault, read by managed identity (fractions of a cent, covered by credit) |
| Terraform state | One small Azure Storage account in the `bootstrap` stack (cents, covered by credit), with a user-assigned managed identity for GitHub OIDC |

6. **Check `terraform plan` output before every apply** and confirm no paid SKUs (no Standard/Premium tiers, no extra replicas, no NAT gateways or public IPs beyond the defaults, and none of: VNet, private endpoint, dedicated workload profile, maintenance window, HA, geo-redundant backup).

### 11.3 Showing the project without paying to host it

- Keep a **demo video and GIF** in the README so recruiters can see it working even when nothing is deployed.
- Publish `docs/RESULTS.md` with the local benchmark numbers.
- In interviews, say the whole environment is created and destroyed with Terraform in minutes. That is a real skill and explains why it is not always online.

Free-tier limits change. Before deploying, confirm the current limits on the Azure free account, Container Apps, and PostgreSQL Flexible Server pricing pages.
