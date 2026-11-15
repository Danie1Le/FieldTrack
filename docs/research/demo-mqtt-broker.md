# Demo MQTT broker without the HiveMQ free tier

Research for issue #14. All sources checked on **2026-11-14**. Container Apps free-grant numbers come from the earlier free-tier research (`docs/research/free-tier-limits.md` on branch `research/free-tier-limits`) and are not re-derived here. Azure prices are East US list prices from the Azure Retail Prices API (`https://prices.azure.com/api/retail/prices`).

Each statement is marked as one of:

- **Documented:** stated on the cited page.
- **Inference:** my conclusion from documented facts, or arithmetic. Not verified by a deploy.

## Short answer

- **Recommended: run Mosquitto as a Container App and connect over MQTT-over-WebSockets (`wss://<app-fqdn>:443`) through normal HTTP ingress.** No VNet, no extra Azure resources, no third-party signup, no data quota. A 4 h demo uses about 2% of the monthly Container Apps free grant. It is the same broker the SPEC already uses locally.
- **External TCP ingress (raw MQTT on 8883) is not an option at $0.** It requires a custom VNet, and a custom VNet adds a billed load balancer and public IPs. That costs about $0.035/h of Students credit and complicates the Terraform. Not worth it when WebSockets work on the free path.
- **Fallback: EMQX Cloud Serverless with spend limit 0.** No card is needed for the free tier, and at spend limit 0 it stops instead of charging. It supports QoS 1, persistent sessions and TLS. But 1 GB/month covers only about 9 h of a 50-device 1 Hz demo, the offline queue holds only 1,000 messages, and the 10 msg/s per-client publish cap rules out the SPEC's flood-mode benchmark.
- **Rejected:** Azure Event Grid MQTT broker (no username/password auth; an offline session is terminated after 100 queued messages, which breaks the NFR-3 kill test), Azure IoT Hub Free (8,000 messages/day), flespi Free (50-session cap is one short of 50 devices plus the ingest worker), public test brokers (no private auth).

## Demo data volume assumptions

Scenario from the issue: **50 devices, 1 msg/s each, 4 h, about 200-byte payloads, QoS 1, one ingest-worker subscriber.**

| Quantity | Value | Basis |
|---|---|---|
| Messages published | 50 × 3,600 × 4 = **720,000** | arithmetic |
| Messages delivered to ingest worker | 720,000 | one subscriber |
| MQTT 3.1.1 PUBLISH size | ≈ 247 B (2 B fixed header + 2 B topic length + ~40 B topic + 2 B packet id + 200 B payload) | MQTT 3.1.1 packet layout; topic length assumed |
| PUBACK size | 4 B | MQTT 3.1.1 |
| Per message, broker in + out, MQTT bytes only | 2 × 247 + 2 × 4 ≈ **502 B** | inference |
| Per message incl. TLS 1.3 record overhead (~22 B per record, 4 records) | ≈ **590 B** | inference; assumes one record per packet, no batching |
| **Total for one 4 h demo** | payload only 288 MB; MQTT bytes ≈ 361 MB; with TLS ≈ **425 MB** | inference |
| Session-minutes (51 clients × 240 min) | **12,240** | arithmetic |

How a provider counts "traffic" (payload, MQTT packet, or TLS bytes) is not documented for any provider checked, so the range 0.29 to 0.43 GB per demo is the honest answer.

## Option 1: Mosquitto on Azure Container Apps (recommended)

### What Container Apps ingress supports (documented)

From [learn: Ingress in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview) (updated 2026-10-22):

- HTTP ingress supports "WebSocket and gRPC", TLS 1.2/1.3 terminated at the ingress point, ports 80 and 443, and an FQDN.
- TCP ingress exists, but: "External TCP ingress is only supported for Container Apps environments that use a virtual network."
- Additional TCP ports "can only be external if the app itself is set as external and the container app is using a virtual network." Up to five additional ports per app. Port 36985 is reserved.
- Internal TCP ingress (reachable by app name from inside the environment) has no VNet requirement stated.

From [learn: Configure ingress in an environment](https://learn.microsoft.com/en-us/azure/container-apps/ingress-environment-configuration):

- Default ingress mode has no billing for the proxy.
- Idle request timeout defaults to **4 minutes**. Changing it needs premium ingress, which runs on a dedicated workload profile (billed).

### Does TCP ingress trigger extra fees? (documented)

From [learn: Configure virtual networks](https://learn.microsoft.com/en-us/azure/container-apps/custom-virtual-networks#managed-resources): when you bring your own VNet, "additional managed resources are created. These resources incur costs." For a workload-profiles environment that is external:

- one Standard static public IP for egress, plus one for ingress, and
- one Standard load balancer.

The VNet must be chosen when the environment is created and cannot be changed later ([learn: Networking](https://learn.microsoft.com/en-us/azure/container-apps/networking)). Minimum subnet `/27`, delegated to `Microsoft.App/environments`.

The [billing doc](https://learn.microsoft.com/en-us/azure/container-apps/billing) lists private endpoints, planned maintenance, and dedicated workload profiles as triggers for the Dedicated Plan Management charge. A custom VNet on its own is **not** listed as a trigger.

Prices (Retail Prices API):

| Meter | Price |
|---|---|
| Standard IPv4 Static Public IP (East US) | $0.005/h |
| Standard Load Balancer, included rules (Global) | $0.025/h |
| Standard Load Balancer, data processed (Global) | $0.005/GB |

**Inference:** TCP ingress costs about 2 × $0.005 + $0.025 = **$0.035/h** (≈ $0.14 per 4 h demo) of Students credit, plus a VNet, subnet and delegation in Terraform. It never charges a card, but it breaks "free grant only" and adds infrastructure for no functional gain over WebSockets.

### Mosquitto WebSockets (documented)

From the [mosquitto.conf man page](https://mosquitto.org/man/mosquitto-conf-5.html):

- `listener <port>` plus `protocol websockets` gives "MQTT tunnelled over WebSockets".
- `persistence` is off by default. When on, sessions, subscriptions and queued messages go to `mosquitto.db` under `persistence_location`. `autosave_interval` defaults to 1800 s.
- `persistent_client_expiration`: default is to never expire persistent clients.
- `max_queued_messages` defaults to 1000; `0` means no maximum. `max_inflight_messages` defaults to 20.
- `allow_anonymous` defaults to false. `password_file` holds users created with `mosquitto_passwd`.

MQTTnet (the SPEC's client) supports WebSocket transport via `WithWebSocketServer(...)`, and TLS turns `ws://` into `wss://` ([MQTTnet wiki: Client](https://github.com/dotnet/MQTTnet/wiki/Client); the wiki examples are for v3, v4+ samples are in the repo).

### Requirement check

| Requirement | Met? | How |
|---|---|---|
| QoS 1 | Yes (documented, Mosquitto core) | |
| Persistent sessions | Yes | `clean_session=false` / MQTT 5 session expiry. In-memory sessions survive an **ingest-worker** restart. They are lost on a **broker** restart unless `persistence true` points at a mounted volume (inference). |
| TLS | Yes, at the edge | Clients use `wss://<fqdn>:443`; Azure terminates TLS 1.2/1.3 with its managed cert. The hop from the ingress proxy to the container is plain WebSocket inside the environment unless peer-to-peer encryption is enabled. |
| No card, $0 | Yes | Free grant only (see below). |

### Free-grant use (inference, using numbers from free-tier-limits.md)

- Broker at 0.25 vCPU / 0.5 GiB for 4 h: 3,600 vCPU-s and 7,200 GiB-s, which is **2%** of the 180,000 vCPU-s and 360,000 GiB-s monthly grant.
- With the API and ingest worker (7,200 vCPU-s per 4 h demo from the earlier research), the whole stack uses 10,800 vCPU-s per 4 h demo, about **6%** of the grant. That leaves roughly 66 h of full-stack demo per month.
- 50 devices × ~300 B/s is well over the 1,000 B/s idle threshold, so the broker bills at the **active** rate while the simulator runs.
- Requests: one HTTP request per WebSocket upgrade (inference), so about 51 per demo against a 2 M grant.
- No data quota. Inbound traffic to Azure is not charged.

### Config rules this implies (inference, not yet tested)

1. Set `min_replicas = 1` and `max_replicas = 1`. MQTT brokers do not share state across replicas, and the default HTTP scale rule could add a second replica and split clients, or scale to zero and drop every session.
2. Set MQTT keepalive below 240 s (MQTTnet's default is 15 s) so the 4-minute ingress idle timeout never fires.
3. Set the external ingress `target_port` to the Mosquitto WebSockets listener (for example 8080) and `transport = "http"` (or `auto`).
4. The ingest worker can skip the edge: it can use an internal additional TCP port (for example `mqtt://mosquitto:1883`) because internal TCP needs no VNet. Alternatively it can use the same `wss://` URL. Verify on first deploy.
5. Set `max_queued_messages` high (or `0`) so the NFR-3 kill test can keep the worker down for minutes without dropping QoS 1 messages. At 50 msg/s the default 1000 fills in 20 s.
6. Turn on `password_file` and `allow_anonymous false`. The FQDN is public.
7. Keep the environment on the default Azure-managed network. Do not add a VNet.

## Option 2: EMQX Cloud Serverless (fallback)

### Quota and card (documented)

| Item | Value | Source |
|---|---|---|
| Free quota / month | 1 M session-minutes, 1 GB traffic (ingress + egress), 1 M rule actions | [emqx.com/pricing](https://www.emqx.com/en/pricing), [docs: Pricing and Billing](https://docs.emqx.com/en/cloud/latest/price/pricing.html) |
| Overage | $2 per M session-minutes, $0.15/GB, $0.25 per M rule actions | same |
| Default spend limit | `0` at creation, meaning free quota only | [docs: Create a Serverless deployment](https://docs.emqx.com/en/cloud/latest/create/serverless.html), [docs: Deployment FAQ](https://docs.emqx.com/en/cloud/latest/faq/deploy.html) |
| At free quota exhausted, spend limit 0 | "Once the free quota is exhausted, the deployment will stop." It stays stopped until next month and does not auto-restart | [docs: Modify spend limit](https://docs.emqx.com/en/cloud/latest/deployments/spend_limit.html), [FAQ](https://docs.emqx.com/en/cloud/latest/faq/deploy.html) |
| Card | Payment method needed only to raise the spend limit above 0. EMQX blog: "No credit card is required" (2023-05-31) | [FAQ](https://docs.emqx.com/en/cloud/latest/faq/deploy.html), [EMQX blog](https://www.emqx.com/en/blog/how-to-get-a-forever-free-serverless-mqtt-service) |
| Deployments per account | 1 (2 with a payment method) | [docs: Quotas and limits](https://docs.emqx.com/en/cloud/latest/create/restriction.html) |
| Idle stop | Stopped after 30 days with no client connections; may be deleted if not re-enabled within 30 more days | [Create a Serverless deployment](https://docs.emqx.com/en/cloud/latest/create/serverless.html) |

### Features and limits (documented)

From [docs: Product plans](https://docs.emqx.com/en/cloud/latest/price/plans.html) and [docs: Quotas and limits](https://docs.emqx.com/en/cloud/latest/create/restriction.html):

- MQTT 3.1, 3.1.1, 5.0. QoS 0, 1, 2. Retained, will, shared subscriptions.
- **TLS only:** MQTT over TLS on 8883 and WSS on 8084. No plain TCP. SNI is required ([FAQ](https://docs.emqx.com/en/cloud/latest/faq/deploy.html)).
- Username/password auth and topic ACLs.
- Max 1,000 concurrent connections. Connection rate 100/s.
- Deployment TPS 1,000. **Per-client publish TPS 10.**
- **Session expiry (MQTT 3.x): 2 hours, not adjustable.** MQTT 5 cap not stated.
- **Max message queue length 1,000, not adjustable.**
- "Durable session" (disk-backed sessions) is not available on Serverless. Ordinary in-memory persistent sessions are (inference from the session-expiry row).

### Fit for a demo (inference)

- **Card:** none needed at spend limit 0, and the deployment stops rather than bills. This fits the $0 rule. Signing up still means a third-party account; not done for this research.
- **Session-minutes:** 12,240 per 4 h demo is 1.2% of the quota. Not a constraint.
- **Traffic:** 0.29 to 0.43 GB per 4 h demo. **Two demos a month fit; a third probably stops the broker mid-demo.** About 9 to 10 h of the 50-device 1 Hz scenario per month at the TLS-inclusive estimate.
- **NFR-3 kill test:** at 50 msg/s the 1,000-message queue covers about 20 s of ingest-worker downtime. A Container Apps restart may take longer. Reduce the device count during that test, or run it locally.
- **Flood mode / 2,000 msg/s benchmark:** blocked by the 1,000 TPS deployment cap and the 10 msg/s per-client cap. It would also use the 1 GB in about 14 minutes. Run benchmarks locally.

## Option 3: other no-card managed brokers

### flespi Free (rejected, narrowly)

From the [flespi restrictions PDF](https://flespi.com/docs/flespi_en_restrictions.pdf) (published 2026-08-12) and the [flespi MQTT broker page](https://flespi.com/mqtt-broker):

- Free plan MQTT limits: **50 active sessions** ("new sessions are discarded"), 200 subscriptions per session, 20,000 MQTT messages per minute per token, 50 MB REST/MQTT traffic per minute, 100 MB persistent-session storage, 100 MB retained storage. Account deleted after 60 days inactive.
- No monthly traffic cap; all limits are per minute.
- MQTT 3.1, 3.1.1, 5.0. QoS 0 and 1 in the spec table. TLS on 8883, secure WebSockets on 443. Persistent sessions with `clean=false` or session expiry. Token as username.
- Card requirement: not stated on the pricing or broker page.

**Inference:** 50 devices plus the ingest worker is 51 sessions, one over the cap. 49 devices would work, and traffic (≈ 6,000 msgs/min, ≈ 0.4 MB/min) fits easily. It is a usable second fallback if the device count can drop, but the card question is unverified and it adds another third-party account.

### Azure Event Grid MQTT broker (rejected)

From [learn: MQTT broker overview](https://learn.microsoft.com/en-us/azure/event-grid/mqtt-overview) and [learn: MQTT feature support](https://learn.microsoft.com/en-us/azure/event-grid/mqtt-support):

- QoS 0 and 1, persistent sessions (MQTT 3.1.1), TLS required, 8883 and WSS on 443.
- Auth is X.509, Microsoft Entra ID, OAuth 2.0 JWT, or custom webhook. **No plain username/password.**
- Max session expiry 1 h default, configurable up to 8 h.
- **"the session queue accumulates messages until it reaches its limit of 100 messages or 1 MB. After the queue reaches its limit during the lifespan of the session, the session is terminated."** At 50 msg/s that is 2 s of ingest-worker downtime. This defeats the NFR-3 kill test.
- Price (Retail Prices API, East US): Standard Throughput Unit $0.04/h; MQTT operations first 1 M/month free, then $1 per M. One 4 h demo is about 1.44 M operations (720 k in + 720 k out), so about $0.44 + $0.16 ≈ **$0.60 of Students credit** per demo (inference). No card, but not free, and the auth model differs from local Mosquitto.

### Azure IoT Hub Free (rejected)

Not re-read in depth. IoT Hub F1 is limited to 8,000 messages/day, and the demo sends 50 msg/s, so it runs out in under 3 minutes (inference). It is also not a general pub/sub broker.

### Public test brokers (rejected)

Brokers such as test.mosquitto.org or broker.emqx.io are shared and open, with no private credentials or guarantees. Not suitable for a demo carrying a portfolio project's data path.

### HiveMQ (still no option)

HiveMQ is offering existing Serverless customers a free 12-month self-managed Enterprise license (100 connections, 100 msg/s), according to an EMQX blog post ([EMQX blog, 2026-10-21](https://www.emqx.com/en/blog/hivemq-cloud-serverless-is-shutting-down)). FieldTrack never had a Serverless cluster, and self-managed means hosting it ourselves anyway, so this does not beat Mosquitto.

## Comparison

| | Mosquitto on Container Apps (WSS) | EMQX Serverless | flespi Free | Event Grid MQTT |
|---|---|---|---|---|
| Card needed | No (Students sub) | No at spend limit 0 | Not stated | No (Students sub) |
| Can it cost money? | No; uses ~2% free grant per 4 h | No; stops at quota | No (inference) | Credit, ≈ $0.60/demo |
| QoS 1 | Yes | Yes | Yes | Yes |
| Persistent sessions | Yes; configurable queue | Yes; 2 h expiry (3.x), queue 1,000 | Yes; 100 MB storage | Yes; ≤ 8 h, **queue 100 msgs then session killed** |
| TLS | Yes (edge, wss:443) | Yes (8883, 8084) | Yes (8883, 443) | Yes (8883, 443) |
| Username/password | Yes | Yes | Token as username | **No** |
| 50 devices + worker | Yes | Yes (1,000 cap) | **No (50 cap)** | Yes |
| Demo data budget | Unlimited | ≈ 2 four-hour demos/month | Per-minute limits only | 1 M ops free/month |
| Flood-mode benchmark | Limited only by 0.25 vCPU | **No** (10 msg/s per client) | No (20 k msg/min) | Costs credit |
| Matches local dev broker | **Same broker and config** | No | No | No |
| Extra signup | None | EMQX account | flespi account | None |

## Recommendation

**Use Mosquitto as a Container App, external HTTP ingress, MQTT over WebSockets on `wss://<fqdn>:443`.** It meets QoS 1, persistent sessions and TLS. It stays inside the Container Apps free grant at about 2% per 4 h demo. It needs no VNet, no extra Azure resources, no card and no third-party account. It is the same broker and config as local docker-compose. Keep the environment on the default managed network: external TCP ingress needs a VNet, and a VNet adds a billed load balancer and two public IPs.

Keep **EMQX Serverless at spend limit 0** as the documented fallback if Container Apps WebSocket ingress turns out not to work with MQTTnet. Budget at most two 4 h, 50-device demos per month on it, and run the kill test and flood benchmark locally.

## Open items to verify on first deploy

- MQTTnet over `wss://` through Container Apps ingress, with the `mqtt` WebSocket subprotocol, end to end. Not tested.
- Whether the ingest worker can reach an internal additional TCP port (1883) in an environment without a VNet. The doc only says external additional ports need a VNet.
- Which `eclipse-mosquitto` image tag ships WebSockets support, and whether it is the newer built-in implementation or the legacy libwebsockets build (the man page mentions both).
- Container App restart time for the ingest worker, to size `max_queued_messages` for the NFR-3 test.
