# The demo runs on the developer's Azure free account, created and destroyed per session

The spec planned Azure for Students, which needs no card. The school account could not be verified (#8), so the demo runs on a regular Azure free account instead: $200 of credit valid from 2026-11-18 to 2026-12-18, in the developer's own billing account. The card on file is for identity only, and nothing is charged unless the account is upgraded, which we never do. The demo is created for a session, recorded, and destroyed. The credit expires on 2026-12-18, so the demo must be recorded before then; after that date the cloud stack is gone and the README shows the recording and `docs/RESULTS.md`.

## Layout

- **Processes:** one Container Apps environment on the Consumption profile only. The API runs with the ingest worker inside, at exactly 1 replica while deployed. The ingest worker must run as a single instance (ADR 0005), the MQTT subscriber stays connected, and SignalR needs no backplane. The simulator is its own Container App at demo rate.
- **Broker:** Mosquitto as a Container App behind HTTPS ingress, reached with MQTT over WebSockets (`wss://…:443`) with password auth. It is the same broker as local, needs no VNet and adds no cost.
- **Dashboard:** the nginx dashboard container as a Container App, proxying `/api` and `/hubs` to the API as docker-compose does. The browser sees one HTTPS address, so there is no CORS and no cross-site cookie question. Static Web Apps is not used.
- **Database:** PostgreSQL Flexible Server B1ms, PostgreSQL 16 with PostGIS, covered by the free account's 12-month hours.
- **Secrets:** Key Vault, read through managed identity.
- **Observability:** Application Insights through the Azure Monitor OpenTelemetry Distro, with sampling and a daily cap on.
- **Images:** GitHub Container Registry, not Azure Container Registry.
- **Terraform:** two stacks. `bootstrap` (state Storage account, a user-assigned managed identity with GitHub federated credentials, budget alerts) is applied once from the laptop and kept. `demo` holds everything else and is applied and destroyed per session from manual GitHub Actions buttons; `terraform plan` runs on PRs. A managed identity is used instead of an Entra app registration because the subscription sits in the university directory, where students may not register apps.

## Cost guardrails

The $0 rule stands: money is spent only from the free credit, and the account is never upgraded to pay-as-you-go.

- No VNet, private endpoint, dedicated workload profile, maintenance window, HA or geo-redundant backup. Each of these triggers a paid feature.
- A budget alert emails at $1 of usage and again near the end of the credit.
- `destroy` runs after every session. Only `bootstrap` stays.
- Before anything is applied, check the account plan (free, not pay-as-you-go) and the remaining credit.
- Load benchmarks stay local, so numbers never depend on cloud capacity.

## Considered Options

- **Azure for Students:** no card needed and renews yearly, but the school account could not be verified.
- **AWS (ECS Fargate, RDS, IoT Core, CloudFront):** chosen briefly (#12) and dropped once the Azure free account was available. IoT Core caps one subscriber connection at 100 msg/s and uses X.509 certificates, so the cloud broker would differ from local Mosquitto. It would also add a second cloud to learn and wire up, while the spec and résumé target Azure.
- **Azure Static Web Apps for the dashboard:** free, but puts the dashboard and API on different sites, which brings CORS and cross-site cookie limits (ADR 0002 sends refresh tokens in the body for that reason). The nginx Container App avoids both.
- **HiveMQ Cloud as the broker:** a free tier exists, but it diverges from local Mosquitto and needs a separate account and credentials.
- **Leave the demo running:** a week of running would drain the credit; destroy-per-session keeps it.

## Consequences

- The cloud demo has a hard deadline, 2026-12-18. After it the free credit is gone and no further cloud runs are possible without paying.
- Every session proves `terraform apply` from empty, and the provisioning time is recorded.
- One API replica means the cloud demo cannot show horizontal scaling.
