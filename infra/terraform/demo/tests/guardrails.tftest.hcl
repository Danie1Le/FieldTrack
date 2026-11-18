# Issue #156 and ADR 0006: the demo stays on free or credit-covered features, keeps passwords out of state, and runs
# exactly one API and broker replica. Plans offline against a mocked azurerm provider; nothing reaches Azure.

mock_provider "azurerm" {
  mock_data "azurerm_resource_group" {
    defaults = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo"
      location = "westus2"
    }
  }

  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id = "00000000-0000-0000-0000-000000000000"
      object_id = "11111111-1111-1111-1111-111111111111"
    }
  }

  # Ids the provider parses when another resource references them (the apply-mode run needs them).
  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo/providers/Microsoft.KeyVault/vaults/kv-fieldtrack-000000"
    }
  }

  mock_resource "azurerm_key_vault_secret" {
    defaults = {
      versionless_id          = "https://kv-fieldtrack-000000.vault.azure.net/secrets/mock"
      resource_versionless_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo/providers/Microsoft.KeyVault/vaults/kv-fieldtrack-000000/secrets/mock"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-fieldtrack-apps"
      principal_id = "22222222-2222-2222-2222-222222222222"
    }
  }

  mock_resource "azurerm_postgresql_flexible_server" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-fieldtrack-000000"
    }
  }

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo/providers/Microsoft.OperationalInsights/workspaces/log-fieldtrack-demo"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo/providers/Microsoft.App/managedEnvironments/cae-fieldtrack-demo"
    }
  }
}

# No real 60 s wait for role propagation in tests.
mock_provider "time" {}

variables {
  admin_email = "admin@example.com"
}

run "stays_on_free_or_credit_covered_features" {
  command = plan

  assert {
    condition     = length(azurerm_container_app_environment.demo.workload_profile) == 0
    error_message = "The Container Apps environment must be Consumption only (no dedicated workload profile)."
  }

  assert {
    condition = (
      azurerm_container_app_environment.demo.infrastructure_subnet_id == null &&
      azurerm_postgresql_flexible_server.demo.delegated_subnet_id == null &&
      azurerm_postgresql_flexible_server.demo.public_network_access_enabled
    )
    error_message = "No VNet or private endpoint: the database is public, limited by its firewall."
  }

  assert {
    condition = (
      azurerm_postgresql_flexible_server.demo.sku_name == "B_Standard_B1ms" &&
      azurerm_postgresql_flexible_server.demo.version == "16" &&
      length(azurerm_postgresql_flexible_server.demo.high_availability) == 0 &&
      length(azurerm_postgresql_flexible_server.demo.maintenance_window) == 0 &&
      !azurerm_postgresql_flexible_server.demo.geo_redundant_backup_enabled
    )
    error_message = "PostgreSQL 16 on B1ms with no HA, maintenance window or geo-redundant backup."
  }

  assert {
    condition = (
      azurerm_postgresql_flexible_server_firewall_rule.azure_services.start_ip_address == "0.0.0.0" &&
      azurerm_postgresql_flexible_server_firewall_rule.azure_services.end_ip_address == "0.0.0.0"
    )
    error_message = "The only firewall rule admits Azure services, not the internet."
  }

  assert {
    condition = (
      azurerm_log_analytics_workspace.demo.daily_quota_gb > 0 &&
      azurerm_application_insights.demo.daily_data_cap_in_gb > 0
    )
    error_message = "Log Analytics and Application Insights need a daily cap."
  }
}

run "lets_the_first_migration_create_postgis" {
  command = plan

  assert {
    condition     = contains(split(",", azurerm_postgresql_flexible_server_configuration.extensions.value), "POSTGIS")
    error_message = "azure.extensions must allow PostGIS."
  }

  assert {
    condition     = [for env in azurerm_container_app.api.template[0].container[0].env : env.value if env.name == "Database__MigrateOnStartup"] == ["true"]
    error_message = "The API must migrate at startup: nothing else runs migrations in the demo."
  }
}

run "runs_one_api_and_one_broker" {
  command = plan

  assert {
    condition = alltrue([
      for app in [azurerm_container_app.api, azurerm_container_app.mosquitto] :
      app.template[0].min_replicas == 1 && app.template[0].max_replicas == 1
    ])
    error_message = "The API (ingest, SignalR) and the broker (MQTT sessions) must run exactly one replica."
  }

  assert {
    condition     = !azurerm_container_app.api.ingress[0].external_enabled
    error_message = "The API is reached only through the dashboard's proxy."
  }
}

# Applied against the mocks: the apps' secret blocks are known only after apply.
run "keeps_passwords_out_of_state" {
  command = apply

  # A value set through value or administrator_password instead (from a non-ephemeral source; validate refuses an
  # ephemeral one there) has no *_wo_version, so these fail. The provider keeps a secret's value out of state only while
  # value_wo_version is set.
  assert {
    condition     = alltrue([for secret in concat(values(azurerm_key_vault_secret.demo), [azurerm_key_vault_secret.db_password]) : secret.value_wo_version >= 1])
    error_message = "Every Key Vault secret needs value_wo_version 1 or more, or its value lands in state."
  }

  assert {
    condition     = azurerm_postgresql_flexible_server.demo.administrator_password_wo_version >= 1
    error_message = "The database password needs administrator_password_wo_version 1 or more."
  }

  assert {
    condition = alltrue(flatten([
      for app in [azurerm_container_app.api, azurerm_container_app.mosquitto, azurerm_container_app.simulator] : [
        for secret in app.secret : secret.key_vault_secret_id != null if secret.name != "appinsights-connection-string"
      ]
    ]))
    error_message = "App secrets other than the Application Insights connection string must be Key Vault references."
  }

  assert {
    condition     = azurerm_key_vault.demo.rbac_authorization_enabled && !azurerm_key_vault.demo.purge_protection_enabled
    error_message = "Key Vault uses RBAC, and destroy must be able to purge it."
  }
}

run "lets_the_simulator_run_unattended" {
  command = plan

  assert {
    condition = (
      [for env in azurerm_container_app.simulator.template[0].container[0].env : env.secret_name if env.name == "FIELDTRACK_ADMIN_TEMPORARY_PASSWORD"] == ["admin-temporary-password"] &&
      [for env in azurerm_container_app.simulator.template[0].container[0].env : env.secret_name if env.name == "FIELDTRACK_ADMIN_PASSWORD"] == ["admin-password"]
    )
    error_message = "The simulator replaces the Admin's temporary password itself, so it needs both."
  }

  assert {
    condition     = strcontains(join(" ", azurerm_container_app.simulator.template[0].container[0].args), "--first-boot clock")
    error_message = "The simulator's state file does not survive a restart; only clock Boots keep new Readings from being duplicates."
  }
}

run "grants_only_the_roles_the_bootstrap_allows" {
  command = plan

  assert {
    condition = (
      azurerm_role_assignment.apps_read_secrets.role_definition_name == "Key Vault Secrets User" &&
      azurerm_role_assignment.deployer_writes_secrets.role_definition_name == "Key Vault Secrets Officer"
    )
    error_message = "The deploy identity may grant only Key Vault Secrets User and Officer (bootstrap condition)."
  }

  assert {
    condition = (
      azurerm_role_assignment.apps_read_secrets.principal_type == "ServicePrincipal" &&
      azurerm_role_assignment.deployer_writes_secrets.principal_type == "ServicePrincipal" &&
      alltrue([for grant in azurerm_role_assignment.broker_reads_mqtt_secrets : grant.principal_type == "ServicePrincipal" && grant.role_definition_name == "Key Vault Secrets User"])
    )
    error_message = "From GitHub, every grant must name a service principal (bootstrap condition)."
  }

  assert {
    condition = (
      toset(keys(azurerm_role_assignment.broker_reads_mqtt_secrets)) == toset(["mqtt-ingest-password", "mqtt-simulator-password"]) &&
      azurerm_container_app.mosquitto.identity[0].identity_ids == toset([azurerm_user_assigned_identity.broker.id])
    )
    error_message = "The internet-facing broker reads only the two MQTT passwords, with an identity of its own."
  }
}
