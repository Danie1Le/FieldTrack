# Stack versions for FieldTrack (as of 2026-11-14)

Research for issue #2: which versions FieldTrack should target now that .NET 8 is out of support, and what breaks against the spec's assumptions (.NET 8, EF Core 8, PostgreSQL 16, Angular 17+).

All facts below come from the owning project's own release, support or package page, read on 2026-11-14. Sources are listed at the end and cited inline as [S1], [S2], and so on.

## Recommendation

| Layer | Spec says | Target now | Latest stable to pin | Supported until |
|---|---|---|---|---|
| .NET runtime / SDK | .NET 8 (LTS) | **.NET 10 (LTS)**, `net10.0`, C# 14 | 10.0.15 (2026-11-10) [S1] | 2028-11-14 [S1] |
| ASP.NET Core (Web API, SignalR, Identity) | 8 | **10** (ships with .NET 10) | 10.0.15 [S1] | 2028-11-14 [S1] |
| EF Core | 8 | **EF Core 10** | 10.0.15 [S4] | 2028-11-10 [S3] |
| Npgsql EF Core provider | (implied 8) | **Npgsql.EntityFrameworkCore.PostgreSQL 10** | 10.0.3 [S5] | follows EF Core 10 |
| Npgsql + NetTopologySuite plugin | (implied 8) | **Npgsql 10 + Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite 10** | 10.0.3 / 10.0.3 [S6][S7] | follows EF Core 10 |
| NetTopologySuite | not pinned | **2.6.0** (pulled in by the plugin) | 2.6.0 (2025-03-12) [S8] | n/a |
| PostgreSQL | 16 | **18** | 18.7 (2026-11-12) [S9][S10] | 2030-11-14 [S9] |
| PostGIS | not pinned | **3.6** (to match Azure); 3.7 is the newest upstream | 3.6.1 on Azure [S12]; 3.7.0 upstream (2026-10-30) [S11] | n/a |
| MQTTnet | not pinned | **MQTTnet 5** | 5.2.0 (2026-07-01) [S15][S16] | n/a |
| Mosquitto | not pinned | **2.x** | 2.1.2 image tag on Docker Hub [S17] | n/a |
| Angular | 17+ | **Angular 22** | 22.2.6 (2026-11-12) [S18][S20] | active to 2027-06, LTS to 2028-06 [S18] |
| Leaflet | not pinned | **Leaflet 1.9.4** (do not adopt 2.0 beta yet) | 1.9.4 [S21][S22] | n/a |
| Geofence drawing | Leaflet.draw | **Replace with Leaflet-Geoman (free)** | @geoman-io/leaflet-geoman-free 2.20.6 (2026-11-04) [S22] | n/a |
| Testcontainers for .NET | not pinned | **4.x** (PostgreSql + Mosquitto modules) | 4.16.0 (2026-10-08) [S24][S25] | n/a |

Short version: **.NET 10 LTS + EF Core 10 + Npgsql 10, PostgreSQL 18 + PostGIS 3.6, MQTTnet 5, Angular 22, Leaflet 1.9.4 with Geoman instead of Leaflet.draw, Testcontainers 4.16.**

## .NET, ASP.NET Core, EF Core

### Support status

The .NET support policy page lists only two supported releases on 2026-11-14 [S1]:

- **.NET 11** (STS): released 2026-11-10, end of support 2028-11-14.
- **.NET 10** (LTS): released 2025-11-11, latest patch 10.0.15 (2026-11-10), end of support 2028-11-14.

.NET 8 **and .NET 9** both reached end of support on 2026-11-10 [S1]. The page states STS releases now get two years of support and LTS releases three [S1]. That is why .NET 11 (STS) and .NET 10 (LTS) end on the same date.

EF Core 10 targets .NET 10 and is supported until 2028-11-10 [S3]. EF Core 8 and 9 both expired on 2026-11-10 [S3]. EF Core 11.0.0 shipped on 2026-11-10 and targets `net11.0` only [S4].

### Why .NET 10 and not .NET 11

- Both end support on the same day (2028-11-14) [S1], so .NET 11 buys no extra support time.
- The Npgsql EF Core provider for EF 11 is **not stable yet**. The latest is `11.0.0-rc.3` (2026-10-28); the latest stable is 10.0.3 [S5]. FieldTrack cannot use EF Core 11 with PostgreSQL until Npgsql ships 11.0.0. The NetTopologySuite plugin is in the same state [S7].
- Moving 10 to 11 later is optional. Do it only if a .NET 11 feature is needed.

### Breaking changes against the spec (EF Core 8 to 10)

EF Core 10's breaking-change page says to also read the EF Core 9 list when coming from 8 [S13]. Items relevant to FieldTrack:

- **Parameterized collections** (`ids.Contains(x.Id)`) now translate to multiple scalar parameters instead of a single JSON/array parameter. Usually fine; can change performance on large lists. `UseParameterizedCollectionMode(...)` or `EF.Parameter(...)` restores the old form [S13].
- **`ExecuteUpdateAsync`** now takes a normal `Func<...>` lambda, not an expression tree. Code that built setter expression trees by hand no longer compiles [S13].
- **EF tools need `--framework`** on multi-targeted projects (medium impact) [S13]. FieldTrack projects should target one framework, so this should not bite.
- **SQL parameter names** are simpler (`@city` instead of `@__city_0`). This breaks SQL snapshot tests and log parsers only [S13].

### Breaking changes in Npgsql 10 (provider and ADO.NET)

- `date` and `time` columns now read as `DateOnly` / `TimeOnly` by default, not `DateTime` / `TimeSpan` [S14]. FieldTrack uses `timestamptz` for `DeviceTimestamp` / `ReceivedAt`, so this matters only if a `date` column is added.
- **Tracing and metric names changed to OpenTelemetry semantic conventions** [S14]. Build dashboards (NFR-7, section 8) against the Npgsql 10 names, not older blog examples.
- TLS validation uses only the supplied root CA, not the system store as well [S14]. Relevant when connecting to Azure PostgreSQL with a custom root certificate.
- EF provider: array `Contains` now translates to `= ANY(...)` by default [S23]. Not used by the current spec.
- No spatial / NetTopologySuite breaking changes are listed [S23].
- PostgreSQL 18 is needed only for virtual generated columns and for mapping `Guid.CreateVersion7()` to the server's `uuidv7()` [S23].

### OpenAPI / Swagger (FR-12)

Since ASP.NET Core 9, the Web API template uses the first-party `Microsoft.AspNetCore.OpenApi` package (`AddOpenApi()` / `MapOpenApi()`) to produce the OpenAPI document [S26]. The template does **not** include a UI. FR-12 ("documented with OpenAPI (Swagger)") still works, but a UI must be added on purpose (for example Swashbuckle's UI, now at 11.0.1, or Scalar). The template also maps the document only in Development [S26]. The spec wants a **public** API docs page (Outputs, item 4), so expose it explicitly in the deployed environment.

## PostgreSQL and PostGIS

### Support status

From the PostgreSQL versioning policy [S9] and the 2026-11-12 release announcement [S10]:

| Major | Current minor | EOL |
|---|---|---|
| 18 | 18.7 | 2030-11-14 |
| 17 | 17.12 | 2029-11-08 |
| 16 | 16.16 | 2028-11-09 |
| 15 | 15.20 | 2027-11-11 |
| 14 | 14.25 | 2026-11-12 (final release) |

**PostgreSQL 19 is not GA.** The 2026-11-12 release shipped **19 RC 2** [S10]. Do not target 19 yet.

### Azure Database for PostgreSQL Flexible Server

- Supports majors 14 to 18. PG 18's current Azure minor is 18.6 [S27]. 11 to 13 are in Extended Support [S27].
- **PostGIS on Azure is 3.6.1 for PG 14 to 18** [S12]. Azure does not offer PostGIS 3.7 yet.
- PG 18 on Azure cannot use `io_method = io_uring`, and some extensions are missing on PG 18 [S27]. PostGIS is available.

### Recommendation

- **PostgreSQL 18 + PostGIS 3.6.** PG 16 (the spec's choice) is still supported until 2028-11-09 and is not broken. PG 18 gives two more years of support, runs on Azure, and is what Npgsql 10's newest features target [S23].
- Upstream PostGIS 3.7.0 was released 2026-10-30 [S11]. Azure tops out at 3.6.1 [S12], so **pin 3.6 locally and in Testcontainers** to keep local benchmarks and the Azure deploy on the same version.
- Docker image tags exist for both: `postgis/postgis:18-3.6` and `postgis/postgis:18-3.7` (also `-alpine`) [S28].

## MQTT: MQTTnet and Mosquitto

- **MQTTnet 5.2.0** is the latest stable (2026-07-01). It ships `net8.0` and `net10.0` builds with no dependencies [S16]. The 5.x line started 2024-12-31 [S16].
- **Breaking changes from 4.x to 5.x** [S15]:
  - **The Managed Client was removed.** The spec's ingest worker must handle reconnects and resubscribes itself (for example, a reconnect loop in the `BackgroundService`). Older tutorials using `ManagedMqttClient` do not apply.
  - Namespaces were renamed.
  - **The client now connects with MQTT 5.0 by default.** Mosquitto 2.x supports 5.0, so this is fine. Set the protocol version explicitly anyway, because the spec says "MQTT 3.1.1/5" and HiveMQ Cloud is used in demos.
  - Support for older .NET Frameworks was dropped.
- QoS 1 with manual acknowledgement (NFR-3) is not listed as changed.
- Mosquitto: the official image publishes `2`, `2.1`, `2.1.2-alpine` and `2.0.22` tags [S17]. Pin `eclipse-mosquitto:2.1`.

## Angular

From the Angular release page [S18] and version compatibility table [S19]:

| Version | Status | Released | Active ends | LTS ends |
|---|---|---|---|---|
| 22 | Active | 2026-06-03 | 2027-06 | 2028-06 |
| 21 | LTS | 2025-11-19 | 2026-06-03 | 2027-06 |
| 20 | LTS | 2025-05-28 | 2025-11-19 | 2026-11-28 |

Angular 2 to 19 are unsupported [S18]. **That includes Angular 17, the spec's floor.** Latest stable is **22.2.6** (2026-11-12); 22.3 is in RC [S20].

Changes that affect the spec's assumptions:

- **The release cadence changed.** Starting with v22, Angular ships one major every 12 months, not every 6 [S18]. v23 is expected about June 2027 [S18].
- **Angular 22 needs TypeScript `>=6.0.0 <6.1.0`** [S19]. npm's `latest` TypeScript is now 7.0.2 [S20], and Angular 22 does not accept it. Pin TypeScript 6.0.x.
- **Node.js `^22.22.3 || ^24.15.0 || ^26.0.0`** [S19]. Use Node 24 in CI.
- **Zoneless change detection is the default from v21** [S29]. `zone.js` is now an optional peer dependency [S20]. Leaflet event callbacks run outside Angular's change detection, so with zoneless the app must use signals or call change detection to update the UI after map events. Plan the map component around signals.
- SignalR client: `@microsoft/signalr` 11.0.0 is current on npm [S20]. The browser client talks to an ASP.NET Core 10 hub without problems because the protocol is stable across versions.

## Leaflet and the geofence drawing plugin

- **Leaflet 1.9.4** is still `latest` on npm [S20]. **Leaflet 2.0** is in beta (`2.0.0-beta.4`, 2026-11-12) [S20][S21]. 2.0 is a large break [S21]:
  - ESM only (UMD dropped).
  - No global `L`.
  - Factory methods (`L.marker(...)`) replaced by constructors.
  - Flat class names (`ZoomControl`).
  - Built-in types replace `@types/leaflet`.
  - Pointer Events replace mouse/touch events.
  - `Class.extend()` replaced by ES classes.
  - Plugins written for 1.x will not work unchanged.
- **Leaflet.draw is effectively abandoned.** Its last npm release is 1.0.4, from 2018-10-24 [S20]. Its README claims support only for "Leaflet 0.7.x and 1.0.0+ branches" [S22a]. It will not work with Leaflet 2.
- **Recommendation:** use **Leaflet 1.9.4 + Leaflet-Geoman (free) 2.20.6** for polygon drawing and editing. Geoman is actively released (six releases since 2026-09-11) and declares peer `leaflet ^1.2.0` [S20]. Revisit Leaflet 2 once it is stable *and* Geoman supports it.

## Testing

- **Testcontainers for .NET 4.16.0** (2026-10-08) [S24][S25]. Packages `Testcontainers.PostgreSql`, `Testcontainers.Mosquitto` and `Testcontainers.XunitV3` are all at 4.16.0 [S20]. Library targets include `net8.0` and `netstandard2.0`, so it runs on .NET 10 [S24].
- Breaking changes inside 4.x [S25]:
  - **4.10.0: builders now require an explicit container image.** Pin images, for example `postgis/postgis:18-3.6` and `eclipse-mosquitto:2.1`. Older samples that call the builder with no image arguments do not compile or run.
  - **4.8.0: wait strategies default to "Running" mode.** If a container exits unexpectedly, the code throws `ContainerNotRunningException`.
  - **4.9.0 / 4.11.0: Docker Engine v29 support.** The Docker API version can be pinned with `DOCKER_API_VERSION`.
- xUnit: `xunit.v3` is at 4.0.3 [S20]. The spec says only "xUnit". Use v3, and pick the matching Testcontainers xUnit v3 package.
- **FluentAssertions 8.x is not free for commercial use.** The NuGet page says 8.x is "free for open-source projects and non-commercial use" and commercial use needs a paid license [S30]. FieldTrack is a public, non-commercial portfolio repo, so this fits the $0 rule today. If that might change, use the Apache-licensed 7.x line or switch to another assertion library (Shouldly, AwesomeAssertions).

## Impact on spec sections

| Spec location | Current text | Should become |
|---|---|---|
| Section 6, Language | C# 12 / .NET 8 (LTS) | C# 14 / .NET 10 (LTS) |
| Section 6, Data access | Entity Framework Core 8 + Npgsql + NetTopologySuite | EF Core 10 + Npgsql 10 + NetTopologySuite 2.6 |
| Section 6 and section 5 diagram, Database | PostgreSQL 16 + PostGIS | PostgreSQL 18 + PostGIS 3.6 |
| Section 6, Frontend | Angular 17+, Leaflet (+ Leaflet.draw) | Angular 22, Leaflet 1.9.4 + Leaflet-Geoman |
| Section 3.2, Ingestion | MQTTnet | MQTTnet 5 (no managed client: own reconnect loop) |
| Section 11.1 | .NET 8 ... | .NET 10 ... |
| FR-12 / Outputs, item 4 | Swagger | Microsoft.AspNetCore.OpenApi + an explicitly added UI, exposed outside Development |

## Sources

Read on 2026-11-14.

- [S1] .NET and .NET Core Support Policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- [S3] EF Core releases and planning: https://learn.microsoft.com/en-us/ef/core/what-is-new/
- [S4] NuGet: Microsoft.EntityFrameworkCore: https://www.nuget.org/packages/Microsoft.EntityFrameworkCore
- [S5] NuGet: Npgsql.EntityFrameworkCore.PostgreSQL: https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL
- [S6] NuGet flat-container index, Npgsql and Npgsql.NetTopologySuite: https://api.nuget.org/v3-flatcontainer/npgsql/index.json, https://api.nuget.org/v3-flatcontainer/npgsql.nettopologysuite/index.json
- [S7] NuGet: Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite: https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite
- [S8] NuGet: NetTopologySuite: https://www.nuget.org/packages/NetTopologySuite
- [S9] PostgreSQL versioning policy: https://www.postgresql.org/support/versioning/
- [S10] PostgreSQL home page / 2026-11-12 release announcement (18.7, 17.12, 16.16, 15.20, 14.25, 19 RC 2): https://www.postgresql.org/
- [S11] PostGIS released versions (Windows): https://postgis.net/documentation/getting_started/install_windows/released_versions/
- [S12] Azure Database for PostgreSQL: extensions by version: https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-versions
- [S13] Breaking changes in EF Core 10: https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes
- [S14] Npgsql 10.0 release notes: https://www.npgsql.org/doc/release-notes/10.0.html
- [S15] MQTTnet GitHub releases: https://github.com/dotnet/MQTTnet/releases
- [S16] NuGet: MQTTnet: https://www.nuget.org/packages/MQTTnet
- [S17] Docker Hub: eclipse-mosquitto tags: https://hub.docker.com/_/eclipse-mosquitto/tags
- [S18] Angular versioning and releases: https://angular.dev/reference/releases
- [S19] Angular version compatibility: https://angular.dev/reference/versions
- [S20] npm registry metadata: https://registry.npmjs.org/@angular/core, https://registry.npmjs.org/@angular/compiler-cli, https://registry.npmjs.org/typescript, https://registry.npmjs.org/leaflet, https://registry.npmjs.org/leaflet-draw, https://registry.npmjs.org/@geoman-io/leaflet-geoman-free, https://registry.npmjs.org/@microsoft/signalr; NuGet flat-container indexes for testcontainers.mosquitto, testcontainers.xunitv3, xunit.v3, swashbuckle.aspnetcore, fluentassertions (https://api.nuget.org/v3-flatcontainer/{id}/index.json)
- [S21] Leaflet GitHub releases (2.0 alpha/beta notes): https://github.com/Leaflet/Leaflet/releases
- [S22] Leaflet-Geoman npm metadata: https://registry.npmjs.org/@geoman-io/leaflet-geoman-free
- [S22a] Leaflet.draw repository README: https://github.com/Leaflet/Leaflet.draw
- [S23] Npgsql EF Core provider 10.0 release notes: https://www.npgsql.org/efcore/release-notes/10.0.html
- [S24] NuGet: Testcontainers.PostgreSql: https://www.nuget.org/packages/Testcontainers.PostgreSql
- [S25] Testcontainers for .NET GitHub releases: https://github.com/testcontainers/testcontainers-dotnet/releases
- [S26] OpenAPI support in ASP.NET Core (aspnetcore-10.0): https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0
- [S27] Supported PostgreSQL versions in Azure Database for PostgreSQL: https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/concepts-supported-versions
- [S28] Docker Hub: postgis/postgis tags: https://hub.docker.com/r/postgis/postgis/tags
- [S29] Angular zoneless guide: https://angular.dev/guide/zoneless
- [S30] NuGet: FluentAssertions: https://www.nuget.org/packages/FluentAssertions

## Caveats

- The .NET policy page gives .NET 10's end of support as 2028-11-14 [S1]. The EF Core page gives EF Core 10's as 2028-11-10 [S3]. Treat November 2028 as the horizon.
- Release dates for GitHub-only listings (MQTTnet, Testcontainers, Leaflet) were cross-checked against NuGet or npm publish timestamps, because the GitHub release pages show day and month without a year.
- Swashbuckle 11.0.1 was taken from the NuGet index only. Its .NET 10 compatibility was not separately verified.
