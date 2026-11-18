# Terraform demo stack

Everything the Azure demo runs, applied from empty for one recorded session and destroyed afterwards (issue #156, layout in [ADR 0006](../../../docs/adr/0006-demo-on-azure-free-account.md), session in #51). The [bootstrap stack](../bootstrap/README.md) must be applied first: it owns the resource group, the state storage and the identity that applies this stack.

## What it creates

All in the bootstrap's resource group `rg-fieldtrack-demo`, which this stack reads and never creates or deletes:

| Resource | Name | Notes |
|---|---|---|
| Container Apps environment | `cae-fieldtrack-demo` | Consumption only (no workload profile), logs to Log Analytics |
| Container App: API | `fieldtrack-api` | Ingest worker inside, exactly 1 replica (ADR 0005), internal ingress only, migrates the database at startup |
| Container App: broker | `fieldtrack-mosquitto` | `docker/mosquitto/cloud`, external HTTPS ingress, MQTT over `wss://…/mqtt`, users `ingest` and `simulator`, exactly 1 replica |
| Container App: dashboard | `fieldtrack-dashboard` | nginx, the one public web address; proxies `/api/` (REST and the SignalR hub) to the API's internal address |
| Container App: simulator | `fieldtrack-simulator` | `demo` at 5 Readings/s over `wss` |
| PostgreSQL Flexible Server | `psql-fieldtrack-<suffix>` | B1ms (1 vCore, 2 GiB), PostgreSQL 16, 32 GiB, `azure.extensions = POSTGIS`, public access limited to Azure services; no HA, geo-redundant backup or maintenance window |
| Key Vault | `kv-fieldtrack-<suffix>` | RBAC mode, purged on destroy; holds every generated secret |
| User-assigned identity | `id-fieldtrack-apps` | Key Vault Secrets User; the apps resolve their Key Vault references with it |
| Log Analytics workspace | `log-fieldtrack-demo` | 30-day retention, daily cap (`log_daily_cap_gb`, default 0.5 GB) |
| Application Insights | `appi-fieldtrack-demo` | Workspace-based, same daily cap; the API gets its connection string and `AzureMonitor__SamplingRatio` |

`<suffix>` is derived from the resource group id, so names are unique per subscription and stable across sessions.

Paid-feature guardrails (ADR 0006): no VNet, private endpoint, dedicated workload profile, maintenance window, HA or geo-redundant backup. `tests/guardrails.tftest.hcl` checks them offline.

### Secrets

Terraform generates every secret with ephemeral `random_password` / `random_bytes` and writes it only through write-only arguments (`value_wo`, `administrator_password_wo`), so no password is in the plan or the state (the read-only plan identity can read state). The apply identity grants itself Key Vault Secrets Officer to write them; the bootstrap's conditional role lets it grant nothing else.

| Key Vault secret | Used by |
|---|---|
| `db-connection-string` | API (`ConnectionStrings__FieldTrack`, TLS `VerifyFull`); the same password is the server's admin password |
| `auth-signing-key` | API (`Auth__SigningKey`, 32 random bytes, base64) |
| `mqtt-ingest-password` | broker and API (`Broker__Password`) |
| `mqtt-simulator-password` | broker and simulator (`FIELDTRACK_MQTT_PASSWORD`) |
| `admin-temporary-password` | API (`FIELDTRACK_ADMIN_PASSWORD`): the first Admin's temporary password |
| `admin-password` | simulator (`FIELDTRACK_ADMIN_PASSWORD`): what the Admin's password is changed to after the first sign-in |
| `demo-password` | API (`FIELDTRACK_DEMO_PASSWORD`); the login page shows it |

To rotate, bump `secrets_version` in `main.tf` and apply: every secret and the database password get new values together.

### Images

Public GHCR images published by #157. Pass an immutable tag (a commit SHA) per session; the defaults track `main`:

| Variable | Default | Built from |
|---|---|---|
| `api_image` | `ghcr.io/danie1le/fieldtrack-api:main` | `src/FieldTrack.Api/Dockerfile` |
| `dashboard_image` | `ghcr.io/danie1le/fieldtrack-dashboard:main` | `web/Dockerfile` (reads `FIELDTRACK_API_URL`) |
| `mosquitto_image` | `ghcr.io/danie1le/fieldtrack-mosquitto:main` | `docker/mosquitto/cloud/Dockerfile` |
| `simulator_image` | `ghcr.io/danie1le/fieldtrack-simulator:main` | `src/FieldTrack.Simulator`: its entrypoint must run the simulator CLI (this stack passes `demo --fleet fleets/demo.json …` as arguments) with `fleets/` in the working directory |

### Broker persistence

The broker keeps its persistence file on the replica's own ephemeral storage, not on an Azure Files share. Mounting `/mosquitto/data` from Azure Files costs little, but Container Apps mounts a share with the storage account key, which would put a shared key into the environment and this stack's state (the bootstrap keeps shared keys off for that reason), and Mosquitto's ownership change on an SMB mount is one more thing to break mid-session. The trade-off: if the broker replica restarts during the session (a new revision or platform maintenance), the ingest worker's persistent session and any messages queued for it are lost. The ingest worker is connected and draining the queue almost all the time, so in practice that is the few messages in flight at the moment of the restart; ingest reconnects and subscribes again on its own. For a one-session demo that is accepted; a long-running deployment would mount a share.

## Apply

Normally from the **Apply demo** workflow on `main` (#157). From the laptop (signed in with `az login` as the subscription owner, with Storage Blob Data Contributor on the state account, see the bootstrap README):

```powershell
cd infra/terraform/demo
terraform init `
  -backend-config="resource_group_name=<TF_STATE_RESOURCE_GROUP>" `
  -backend-config="storage_account_name=<TF_STATE_STORAGE_ACCOUNT>" `
  -backend-config="container_name=<TF_STATE_CONTAINER>"
terraform apply -var "admin_email=<your email>" -var "deployer_principal_type=User"
```

The `<...>` values are the bootstrap outputs. `admin_email` has no default, so no address is committed; put it in a gitignored `terraform.tfvars` if you prefer. Outputs: `dashboard_url`, `mqtt_url` (`wss://…/mqtt`) and `key_vault_name`.

## After apply

The API seeds the first Admin with a temporary password that must be replaced at the first sign-in (#61). The simulator signs in as that Admin with `admin-password`, so it fails and restarts until you have done this once:

1. Give yourself read access to the vault (Key Vault is in RBAC mode, so even the subscription owner needs a data role; the deploy identity cannot grant roles to users):

   ```powershell
   $vault = terraform output -raw key_vault_name
   az role assignment create --role "Key Vault Secrets User" --assignee (az ad signed-in-user show --query id -o tsv) `
     --scope (az keyvault show --name $vault --query id -o tsv)
   ```

2. Read the two Admin passwords (they stay in your terminal; do not paste them anywhere else):

   ```powershell
   az keyvault secret show --vault-name $vault --name admin-temporary-password --query value -o tsv
   az keyvault secret show --vault-name $vault --name admin-password --query value -o tsv
   ```

3. Open `dashboard_url`, sign in as `admin_email` with the temporary password, and set the new password to `admin-password`.
4. The simulator succeeds on its next restart (or restart it: `az containerapp revision restart`); the demo Scenario then plays on the dashboard within five minutes.

Visitors sign in as the Demo Operator, whose email and password the login page shows.

## The session (#51)

One session only:

1. Check the remaining credit, apply the bootstrap stack, then **Apply demo**. Record the time from apply until the dashboard answers.
2. Do the steps in [After apply](#after-apply), run the demo, record the evidence. On this first deploy also check: the WebSocket idle behaviour of MQTT and SignalR through ingress (request timeout 240 s), a 31-day CSV export through ingress, that migrations ran (`SELECT * FROM "__EFMigrationsHistory"`, `SELECT extversion FROM pg_extension WHERE extname = 'postgis'`, expect 3.6.x), and the Postgres memory against the Reading key index (`SHOW shared_buffers`, `pg_relation_size` of the key table's index).
3. **Destroy demo**, record the time, and confirm the group is empty: `az resource list --resource-group rg-fieldtrack-demo --output table`.
4. Destroy the bootstrap stack (its README: Tear down).

## Destroy

```powershell
terraform destroy -var "admin_email=<your email>" -var "deployer_principal_type=User"
```

Destroy removes everything above, purges the Key Vault (soft delete would otherwise hold its name for 7 days) and leaves the empty resource group to the bootstrap stack. Nothing billable remains: confirm in the portal (the group lists no resources) and under **Cost Management** the next day.

## Checks

CI's `terraform` job runs, for every stack under `infra/terraform`: `terraform fmt -check`, `init -backend=false`, `validate` and `terraform test` (offline, mocked azurerm provider).
