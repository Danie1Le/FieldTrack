# Free-tier limits for demo hosting

Research for issue #4. All sources checked on **2026-11-14**. Prices are USD list prices for **East US** from the Azure Retail Prices API (`https://prices.azure.com/api/retail/prices`), because the azure.microsoft.com pricing pages render every rate as a `$-` placeholder to unauthenticated readers. Other regions differ.

## Short answer

- **Azure (Students credit, Container Apps, B1ms Postgres, Static Web Apps, App Insights): fits the $0 rule for demo-only deploys.** None of these can charge a card on an Azure for Students subscription. The worst case is burning some of the $100 credit.
- **HiveMQ Cloud free tier: gone.** HiveMQ retires the Serverless plan (the free one) on 2026-12-31 and no longer lets anyone create Serverless clusters. Its only cloud plan for new users is Starter, a 15-day trial followed by about $0.34/hour. The MQTT broker row in SPEC section 11.2 needs a new choice.
- **Static Web Apps Free cannot link the Container Apps API under `/api`.** Bring-your-own backends are Standard-plan only. The Angular app must call the Container Apps URL directly (CORS).
- **PostGIS works everywhere checked** (Azure Flexible Server, Neon, Supabase). **pg_partman** is supported on Azure and Neon; on Supabase it is documented but its availability on Postgres 17 projects is unclear. Native declarative partitioning works on all three.
- **An always-on container uses up the Container Apps free grant in about 8 days** at the smallest size (0.25 vCPU / 0.5 GiB). That is fine for demo-only deploys, but not for keeping anything running between demos.

## 1. Azure for Students

| Item | Value | Source |
|---|---|---|
| Credit | $100, valid 12 months, renewable each year while a student | [learn: About Azure for Students](https://learn.microsoft.com/en-us/azure/education-hub/about-azure-for-students), [azure.microsoft.com/free/students](https://azure.microsoft.com/en-us/free/students/) |
| Credit card | Not required | same |
| When credit runs out | "your Azure subscription will be disabled". Upgrading to Pay-As-You-Go needs a call to Azure Support | [Offer terms MS-AZR-0170P](https://azure.microsoft.com/en-us/pricing/offers/ms-azr-0170p/) |
| Credit exclusions | Support plans, Azure DevOps, Visual Studio subscriptions, ExpressRoute, Marketplace and third-party products | same |
| Free services listed for students | PostgreSQL Flexible Server: 750 h/month B1MS + 32 GB storage + 32 GB backup storage (12 months). Container Apps: 180,000 vCPU-s, 360,000 GiB-s, 2 M requests (always free). Static Web Apps: 100 GB bandwidth per subscription, 2 custom domains, 0.5 GB storage per app (always free) | [azure.microsoft.com/free/students](https://azure.microsoft.com/en-us/free/students/) |

**Charge flags**
- The students page says some 12-month free amounts are for new Azure customers only. The general free-services page says "12 months free services is available only to new customers who have not previously had an Azure account" ([free services](https://azure.microsoft.com/en-us/pricing/free-services)). If the account existed before, assume B1ms hours come out of credit. That costs credit, not money.
- Overage on any service draws down credit. It cannot charge money unless someone upgrades to Pay-As-You-Go or adds a card. Keep SPEC rule 11.2.2.
- One Azure for Students subscription per person.

## 2. Azure Container Apps (consumption plan)

**Free grant, per subscription per calendar month:** 180,000 vCPU-seconds, 360,000 GiB-seconds, and 2 million HTTP requests. Free usage does not appear on the bill. ([learn: Billing](https://learn.microsoft.com/en-us/azure/container-apps/billing), [pricing page](https://azure.microsoft.com/en-us/pricing/details/container-apps/))

**Rates after the grant** (East US, Retail Prices API, meters effective since 2022-06-01):

| Meter | Price |
|---|---|
| vCPU active | $0.000024 per vCPU-second |
| Memory active | $0.000003 per GiB-second |
| vCPU idle | $0.000003 per vCPU-second |
| Memory idle | $0.000003 per GiB-second |
| Requests | $0.40 per million |

**Billing rules** ([learn: Billing](https://learn.microsoft.com/en-us/azure/container-apps/billing)):
- Scaled to zero: no resource charge.
- `min_replicas > 0` and scaled to the minimum: a replica is billed at the idle rate only while it is processing no HTTP requests, using < 0.01 vCPU, and receiving < 1,000 bytes/s. Otherwise it is billed at the active rate.
- Only requests from outside the environment are billable. Health probes are not billable.

**What an always-on replica uses** (30-day month = 2,592,000 s):

| Replica size | vCPU-s / month | GiB-s / month | Free grant lasts | Overage if active all month | Overage if idle all month |
|---|---|---|---|---|---|
| 0.25 vCPU / 0.5 GiB | 648,000 | 1,296,000 | 720,000 s ≈ 200 h ≈ 8.3 days | ≈ $11.23 vCPU + $2.81 mem ≈ **$14.04** | ≈ $1.40 + $2.81 ≈ **$4.21** |
| 0.5 vCPU / 1 GiB | 1,296,000 | 2,592,000 | 360,000 s = 100 h ≈ 4.2 days | ≈ $26.78 + $6.70 ≈ **$33.48** | ≈ $3.35 + $6.70 ≈ **$10.05** |

The grant is shared by every app in the subscription. An API plus an ingest worker, both at 0.25 vCPU and always on, get about 100 h each per month. An MQTT ingest worker holds a subscription and receives telemetry, so expect it to bill at the **active** rate while the simulator runs.

A demo-only deploy (for example 4 h, two 0.25 vCPU apps) uses about 7,200 vCPU-s, which is 4% of the grant. **This fits $0.** Leaving the stack up between demos does not.

**Charge flags**
- **Environment Management Hour, $0.10/h (East US, effective 2026-09-01).** The billing doc says private endpoints and planned maintenance trigger the Dedicated Plan Management charge "regardless of whether you use the Consumption or Dedicated plans". A dedicated workload profile triggers it too. Microsoft announced a label-only rename for December 2026, with "pricing, billing behavior, and the total amount customers pay are unchanged" ([microsoft/azure-container-apps#1886](https://github.com/microsoft/azure-container-apps/issues/1886)). **In Terraform, use only the built-in Consumption profile. Do not configure a planned maintenance window, private endpoints, or a dedicated workload profile.** A Microsoft Q&A thread reports this fee on a Consumption-only environment after a maintenance window was set ([Q&A](https://learn.microsoft.com/en-us/answers/questions/6066720/is-a-planned-maintenance-window-honored-and-billed)).
- A custom VNet or other Azure resources can add charges ([learn: Billing](https://learn.microsoft.com/en-us/azure/container-apps/billing)).
- Create the environment as Workload profiles (v2), the default, and run apps on the Consumption profile. An unused consumption profile costs nothing ([learn: Structure](https://learn.microsoft.com/en-us/azure/container-apps/structure)).
- The Container Apps environment sends logs to Log Analytics. That ingestion counts toward the 5 GB allowance in section 5.

## 3. Azure Database for PostgreSQL Flexible Server, Burstable B1ms

| Item | Value | Source |
|---|---|---|
| B1ms compute (1 vCore, 2 GiB) | **$0.017/hour** ≈ $12.41 per 730 h | Retail Prices API, product "Azure Database for PostgreSQL Flexible Server Burstable BS Series Compute", sku `B1MS` |
| Storage (Premium SSD) | $0.115 per GB-month (32 GB ≈ $3.68/month) | Retail Prices API, "Flex Server Storage / Storage Data Stored" |
| Backup storage (LRS) | $0.095 per GB-month | Retail Prices API |
| Free amount | 750 h/month B1MS + 32 GB storage + 32 GB backup, for 12 months | [azure.microsoft.com/free/students](https://azure.microsoft.com/en-us/free/students/) |
| Supported PG versions | 11 to 18 | [learn: Quickstart](https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server) |

750 h covers one server running all month (a 31-day month is 744 h). Several servers share the 750 h. Even without the free hours, a 4 h demo costs about $0.07 of credit.

**Extensions on PostgreSQL 16** ([learn: Extensions by name](https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-versions), updated 2026-07-13):

| Extension | PG 16 | PG 17 | Needs `shared_preload_libraries` |
|---|---|---|---|
| `postgis` (+ `postgis_topology`, `postgis_raster`, etc.) | 3.6.1 | 3.6.1 | No |
| `pg_partman` | 5.4.3 | 5.4.3 | **Yes** |
| `pg_partman_bgw` | 5.0.1 | 5.0.1 | **Yes** |
| `pg_cron` | 1.6 | 1.6 | **Yes** |

Every extension must first be allowlisted in the `azure.extensions` server parameter before `CREATE EXTENSION` will work ([learn: Allow extensions](https://learn.microsoft.com/en-us/azure/postgresql/extensions/how-to-allow-extensions)). In Terraform, use `azurerm_postgresql_flexible_server_configuration` for `azure.extensions` and `shared_preload_libraries`. A preload change needs a server restart.

**Charge flags.** These settings move you past the free amount and draw down credit:
- Any SKU other than B1ms.
- Storage above 32 GB, or storage autogrow pushing it past 32 GB.
- High availability (same-zone or zone-redundant standby).
- Geo-redundant backup, or backup storage above 32 GB.
- A second server running at the same time.

## 4. Azure Static Web Apps, Free plan

| Limit | Free plan | Source |
|---|---|---|
| Bandwidth | 100 GB/month per subscription. **Overage "Unavailable"** (Standard: $0.20/GB) | [learn: Quotas](https://learn.microsoft.com/en-us/azure/static-web-apps/quotas) |
| Apps per subscription | 10 | same |
| Storage | 250 MB per environment, 500 MB total | same |
| Preview environments | 3 | same |
| Custom domains | 2 | same |
| Request size | 30 MB | same |
| SSL | Free, auto-renewing | [learn: Plans](https://learn.microsoft.com/en-us/azure/static-web-apps/plans) |
| SLA | None | same |
| Private endpoints | Not available | same |
| APIs | Managed Azure Functions only. **"Bring your own APIs is only available in the Azure Static Web Apps Standard plan."** This covers Container Apps, App Service, API Management, and linked Functions | [learn: API overview](https://learn.microsoft.com/en-us/azure/static-web-apps/apis-overview) |

**Charge flags**
- The Free plan has no overage billing, so it cannot generate a charge. Only switching to Standard can.
- **Design impact:** the Angular app cannot reach the API through SWA's `/api` route on Free. Point it at the Container Apps FQDN and configure CORS on the API. SignalR WebSockets go direct in any case, because SWA APIs are HTTP-only with a 45 s request limit.

## 5. Application Insights and Log Analytics

| Item | Value | Source |
|---|---|---|
| Free ingestion | "The first 5 GB/month per billing account" (Analytics Logs, Pay-As-You-Go tier) | [Azure Monitor pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/) |
| Price above free | $2.30/GB Analytics Logs ingestion (East US) | Retail Prices API, "Log Analytics / Analytics Logs Data Ingestion", tier from 5 GB |
| Included retention | 90 days for workspace-based Application Insights. 31 days for Analytics Logs | [Azure Monitor pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/) |
| Extra retention | $0.10 per GB-month | Retail Prices API |
| Default sampling | The Microsoft OpenTelemetry Distro (.NET) defaults to rate-limited sampling of 5 traces/second. A fixed ratio is also supported | [learn: OTel sampling](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-sampling) |
| Daily cap | Supported. It stops ingestion until the next UTC day | same; [learn: Daily cap](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/daily-cap) |

**Charge flags**
- The 5 GB allowance is per **billing account** and shared with the Container Apps environment's console and system logs.
- Set a workspace daily cap of about 0.15 GB/day (5 GB / 31). Export only `ERROR`/`WARN` logs. Keep trace sampling on. Metrics are not sampled.

## 6. HiveMQ Cloud

| Item | Value | Source |
|---|---|---|
| Serverless plan (the free tier: 100 connections) | **Retired 2026-12-31. "You can no longer create a Serverless cluster."** After that date clients cannot connect, and HiveMQ deletes the clusters and their data | [HiveMQ docs: Serverless end of life](https://docs.hivemq.com/hivemq-cloud/serverless-end-of-life.html), [HiveMQ Cloud intro](https://docs.hivemq.com/hivemq-cloud/introduction.html) |
| Serverless limits (historical) | Up to 100 connections free, one permission per MQTT credential, multi-tenant, no SLA, no Control Center (blog dated 2026-02-27) | [HiveMQ blog](https://www.hivemq.com/blog/hivemq-cloud-serverless-vs-starter-mqtt-iot-or-iiot) |
| Starter (current cloud entry plan) | 15-day trial, then "From $0.34/hour" plus "$0.80/million messages". 10,000 connections, up to 1 MB/s, TLS, auth and RBAC included. MQTT 3.1/3.1.1/5 over TLS and WebSockets | [hivemq.com/pricing](https://www.hivemq.com/pricing/) |
| Lab (free, self-hosted) | 1 broker, 25 connections, 25 msg/s, 30-day license renewable, no credit card | same |

**Charge flag: HiveMQ Cloud no longer has a $0 managed option for new users.** Starter costs about $8/day after the trial. Whether the Starter trial asks for a card is not stated. Do not sign up.

Candidate replacements for a follow-up decision (not researched in depth):
- **EMQX Cloud Serverless free quota:** 1 M session-minutes/month, 1 GB traffic/month (ingress + egress), up to 1,000 sessions, 1 M rule actions ([emqx.com/pricing](https://www.emqx.com/en/pricing)). Overage is listed at $2 per M session-minutes and $0.15/GB. Whether a card is required or a spend limit applies is not stated. 1 GB/month is tight: 50 devices sending 200 B/s each is about 860 MB/day.
- **Mosquitto in Container Apps:** uses the free grant (see section 2: about 200 h/month at 0.25 vCPU). The broker would need TCP ingress or MQTT-over-WebSockets on HTTP ingress. That still needs checking.

## 7. Neon Free plan

| Item | Value | Source |
|---|---|---|
| Card | Not required. The plan is permanent, not a trial | [neon.com/pricing](https://neon.com/pricing) |
| Projects | Up to 100 | same |
| Compute | 100 CU-hours per project per month. Autoscaling up to 2 CU (8 GB RAM) | same |
| Scale to zero | After 5 min idle. Always on for Free and cannot be turned off | same |
| Storage | 1 GB per project, 20 GB across all projects | same |
| Egress | 5 GB per project per month | same |
| Branches | 10 per project | same |
| Over limit | CU-hours or egress exhausted: compute suspended until the next period. Storage over limit: writes blocked. No data deleted, no charge | same |
| PostGIS | 3.3.10 on PG 16, 3.5.7 on PG 17 | [Neon docs: Supported extensions](https://neon.com/docs/extensions/pg-extensions) |
| pg_partman | 5.1.0 on PG 14 to 18. `pg_partman_bgw` preloaded by default | same |
| pg_cron | 1.6. "runs only while the compute is active, so it needs scale to zero disabled", which is not possible on Free | same |
| Declarative partitioning | Native Postgres, no extension needed | (Postgres core) |

At 0.25 CU always on, a month is about 182 CU-h, which is over the 100 CU-h quota. With scale to zero that is fine. On Free, run pg_partman maintenance from the app or ingest worker, not from pg_cron.

## 8. Supabase Free plan

| Item | Value | Source |
|---|---|---|
| Active projects | 2 | [supabase.com/pricing](https://supabase.com/pricing) |
| Database size | 500 MB per project | same |
| Compute | Nano: shared CPU, up to 0.5 GB RAM, 60 direct connections, pooler 200 clients | [Supabase docs: Compute and disk](https://supabase.com/docs/guides/platform/compute-and-disk) |
| Egress | 5 GB + 5 GB cached | [supabase.com/pricing](https://supabase.com/pricing) |
| Pausing | "Free projects are paused after 1 week of inactivity" | same |
| Card / over-limit behavior | Not stated on the pricing or billing pages | [Supabase docs: Billing](https://supabase.com/docs/guides/platform/billing-on-supabase) |
| PostGIS | Supported. Install into the `extensions` schema | [Supabase docs: PostGIS](https://supabase.com/docs/guides/database/extensions/postgis) |
| pg_partman | A docs page exists (maintenance via pg_cron). Supabase dropped it from the first Postgres 17 images and said it would return later. Availability on a new PG 17 project is **unconfirmed** | [Supabase docs: pg_partman](https://supabase.com/docs/guides/database/extensions/pg_partman), [PG 17 release notes discussion](https://github.com/orgs/supabase/discussions/35851) |
| Declarative partitioning | Supported (native `PARTITION BY RANGE`) | [Supabase docs: Partitions](https://supabase.com/docs/guides/database/partitions) |

500 MB fills fast with telemetry: about 1 M rows at roughly 200 B per row plus indexes. Keep demo data small or prune partitions.

## Ranking for the database fallback

1. **Azure B1ms.** It matches the production design: PostGIS 3.6.1, pg_partman 5.4.3, and pg_cron, at $0 within 750 h.
2. **Neon.** No card needed, PostGIS and pg_partman available, 1 GB storage. Run partition maintenance from the app.
3. **Supabase.** 500 MB, pauses after 7 days, pg_partman uncertain on PG 17.

## Things to re-check before each deploy

The Retail Prices API query used for the rates in this note:

```
https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Azure Container Apps' and armRegionName eq 'eastus' and priceType eq 'Consumption'
```

Swap `serviceName` for `Azure Database for PostgreSQL` or `Log Analytics` to get the other rates.
