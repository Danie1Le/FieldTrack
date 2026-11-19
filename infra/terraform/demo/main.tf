# The demo stack (#156, layout in ADR 0006): everything one recorded session needs, applied from empty and destroyed
# afterwards. Paid-feature guardrails (ADR 0006): no VNet, private endpoint, dedicated workload profile, maintenance
# window, HA or geo-redundant backup anywhere in this stack.

data "azurerm_client_config" "current" {}

# Owned by the bootstrap stack, whose role-granting identity is scoped to it: read, never created or deleted here.
data "azurerm_resource_group" "demo" {
  name = var.resource_group_name
}

locals {
  # var.location, not the resource group's: a group's location does not constrain its resources (see variables.tf).
  location = var.location
  # Key Vault and PostgreSQL names are global DNS names; a suffix fixed per subscription keeps them unique and stable.
  suffix = substr(sha1(data.azurerm_resource_group.demo.id), 0, 6)

  database_login = "fieldtrack"

  # Key Vault secret name => value. The values come from ephemeral resources and reach Azure only through write-only
  # arguments, so no password is ever in plan or state (the read-only plan identity can read state).
  secret_values = {
    "db-connection-string" = join(";", [
      "Host=${azurerm_postgresql_flexible_server.demo.fqdn}",
      "Database=fieldtrack",
      "Username=${local.database_login}",
      "Password=${ephemeral.azurerm_key_vault_secret.db_password.value}",
      # Azure requires TLS; check the server's certificate and name too.
      "SSL Mode=VerifyFull",
      # The aspnet image has no Kerberos library (docker-compose.yml says the same).
      "Gss Encryption Mode=Disable",
    ])
    "auth-signing-key"         = ephemeral.random_bytes.signing_key.base64
    "mqtt-ingest-password"     = ephemeral.random_password.secret["mqtt-ingest"].result
    "mqtt-simulator-password"  = ephemeral.random_password.secret["mqtt-simulator"].result
    "admin-temporary-password" = ephemeral.random_password.secret["admin-temporary"].result
    "admin-password"           = ephemeral.random_password.secret["admin"].result
    "demo-password"            = ephemeral.random_password.secret["demo"].result
  }

  # Bump to write freshly generated values for every secret and the database password on the next apply. Never 0: the
  # provider keeps a secret's value out of state only while its value_wo_version is non-zero.
  secrets_version = 1

  # The broker reads only these.
  broker_secrets = ["mqtt-ingest-password", "mqtt-simulator-password"]
  # The simulator reads only these.
  simulator_secrets = ["admin-temporary-password", "admin-password", "mqtt-simulator-password"]
}

# --- Secrets ---------------------------------------------------------------------------------------------------------

# New values on every run, but write-only arguments send them only when secrets_version changes (or on create). The
# database password is therefore drawn once into Key Vault and read back from there (below).
ephemeral "random_password" "secret" {
  for_each = toset(["db", "mqtt-ingest", "mqtt-simulator", "admin-temporary", "admin", "demo"])

  length = 24
  # Identity's default rules want all four kinds; PostgreSQL wants three. "-" and "_" need no quoting in a connection
  # string, an environment variable or mosquitto_passwd's arguments.
  override_special = "-_"
  min_lower        = 1
  min_upper        = 1
  min_numeric      = 1
  min_special      = 1
}

# The API refuses a signing key under 256 bits.
ephemeral "random_bytes" "signing_key" {
  length = 32
}

resource "azurerm_key_vault" "demo" {
  name                = "kv-fieldtrack-${local.suffix}"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"
  # Data access through Azure RBAC roles only; the bootstrap lets the deploy identity grant just the two secret roles.
  rbac_authorization_enabled = true
  # Destroy purges the vault (azurerm's default), so its name is free again for the next apply.
  purge_protection_enabled   = false
  soft_delete_retention_days = 7
}

# The API's identity reads secrets; Container Apps resolves the API's Key Vault references with it.
resource "azurerm_user_assigned_identity" "apps" {
  name                = "id-fieldtrack-apps"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
}

resource "azurerm_role_assignment" "apps_read_secrets" {
  scope                = azurerm_key_vault.demo.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.apps.principal_id
  principal_type       = "ServicePrincipal"
}

# Whoever applies writes the secrets.
resource "azurerm_role_assignment" "deployer_writes_secrets" {
  scope                = azurerm_key_vault.demo.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
  principal_type       = var.deployer_principal_type
}

# New role assignments take a while to reach Key Vault's data plane; writing or resolving a secret before then fails
# with 403.
resource "time_sleep" "role_propagation" {
  create_duration = "60s"
  depends_on      = [azurerm_role_assignment.apps_read_secrets, azurerm_role_assignment.deployer_writes_secrets]
}

resource "azurerm_key_vault_secret" "demo" {
  for_each = toset(keys(local.secret_values))

  name             = each.key
  key_vault_id     = azurerm_key_vault.demo.id
  value_wo         = local.secret_values[each.key]
  value_wo_version = local.secrets_version

  depends_on = [time_sleep.role_propagation]
}

# The one source of the database password. The server and db-connection-string both take the value read back from
# here, so a re-apply after a failed one cannot leave them different: a fresh ephemeral password is drawn every run,
# but this secret is written only on create or when secrets_version changes.
resource "azurerm_key_vault_secret" "db_password" {
  name             = "db-password"
  key_vault_id     = azurerm_key_vault.demo.id
  value_wo         = ephemeral.random_password.secret["db"].result
  value_wo_version = local.secrets_version

  depends_on = [time_sleep.role_propagation]
}

# Opened again whenever it is needed and never stored. Pinned to the version just written, so a rotation reads its new
# value. Reading it needs Key Vault data access, which the read-only plan identity lacks (README: Checks).
ephemeral "azurerm_key_vault_secret" "db_password" {
  name         = azurerm_key_vault_secret.db_password.name
  key_vault_id = azurerm_key_vault.demo.id
  version      = azurerm_key_vault_secret.db_password.version
}

# The internet-facing broker gets an identity of its own that can read only its two passwords, so code running in it
# cannot fetch the signing key, the connection string or the Admin's password.
resource "azurerm_user_assigned_identity" "broker" {
  name                = "id-fieldtrack-broker"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
}

resource "azurerm_role_assignment" "broker_reads_mqtt_secrets" {
  for_each = toset(local.broker_secrets)

  scope                = azurerm_key_vault_secret.demo[each.key].resource_versionless_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.broker.principal_id
  principal_type       = "ServicePrincipal"
}

# Likewise the simulator gets an identity that can read only the Admin's two passwords and its own MQTT password, not
# the signing key, the connection string or the ingest password.
resource "azurerm_user_assigned_identity" "simulator" {
  name                = "id-fieldtrack-simulator"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
}

resource "azurerm_role_assignment" "simulator_reads_its_secrets" {
  for_each = toset(local.simulator_secrets)

  scope                = azurerm_key_vault_secret.demo[each.key].resource_versionless_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.simulator.principal_id
  principal_type       = "ServicePrincipal"
}

# Per-secret grants need the secrets, so they wait in a sleep of their own.
resource "time_sleep" "per_secret_role_propagation" {
  create_duration = "60s"
  depends_on      = [azurerm_role_assignment.broker_reads_mqtt_secrets, azurerm_role_assignment.simulator_reads_its_secrets]
}

# --- Database --------------------------------------------------------------------------------------------------------

resource "azurerm_postgresql_flexible_server" "demo" {
  name                = "psql-fieldtrack-${local.suffix}"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  # Burstable B1ms (1 vCore, 2 GiB), PostgreSQL 16: covered by the free account's 12-month hours (ADR 0006).
  sku_name   = "B_Standard_B1ms"
  version    = "16"
  storage_mb = 32768 # the smallest size Flexible Server offers

  administrator_login               = local.database_login
  administrator_password_wo         = ephemeral.azurerm_key_vault_secret.db_password.value
  administrator_password_wo_version = local.secrets_version

  # Public access, limited by the firewall rule below; no VNet or private endpoint (paid-feature guardrails).
  public_network_access_enabled = true
  backup_retention_days         = 7
  geo_redundant_backup_enabled  = false
  auto_grow_enabled             = false
  # No high_availability or maintenance_window block: both are paid features.
}

# PostGIS must be allow-listed before the API's first migration runs CREATE EXTENSION postgis.
resource "azurerm_postgresql_flexible_server_configuration" "extensions" {
  name      = "azure.extensions"
  server_id = azurerm_postgresql_flexible_server.demo.id
  value     = "POSTGIS"
}

# 0.0.0.0 to 0.0.0.0 is Azure's "allow public access from Azure services" rule. It admits every Azure IP address, other
# tenants' VMs and functions included, not only this environment: the 24-character password and TLS (VerifyFull)
# protect the server, not network isolation. Acceptable for a demo that lives a few hours.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "AllowAllAzureServicesAndResourcesWithinAzureIps"
  server_id        = azurerm_postgresql_flexible_server.demo.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# --- Observability ---------------------------------------------------------------------------------------------------

resource "azurerm_log_analytics_workspace" "demo" {
  name                = "log-fieldtrack-demo"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  sku                 = "PerGB2018"
  retention_in_days   = 30 # the minimum, inside the free retention period
  daily_quota_gb      = var.log_daily_cap_gb
}

# Workspace-based, so the effective cap is the lower of the two caps (Azure Monitor docs); both get the same value.
resource "azurerm_application_insights" "demo" {
  name                 = "appi-fieldtrack-demo"
  location             = local.location
  resource_group_name  = data.azurerm_resource_group.demo.name
  workspace_id         = azurerm_log_analytics_workspace.demo.id
  application_type     = "web"
  daily_data_cap_in_gb = var.log_daily_cap_gb
}
