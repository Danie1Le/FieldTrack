# Container Apps (ADR 0006). Every app runs exactly one replica: the API because the ingest worker must be a single
# instance and SignalR has no backplane (ADR 0005), the broker because two replicas would split MQTT sessions, the
# others because one is enough for a demo.

# Consumption only: no workload_profile block, so no dedicated (paid) profile can be added by accident.
resource "azurerm_container_app_environment" "demo" {
  name                       = "cae-fieldtrack-demo"
  location                   = local.location
  resource_group_name        = data.azurerm_resource_group.demo.name
  logs_destination           = "log-analytics"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.demo.id
}

locals {
  identity_id = azurerm_user_assigned_identity.apps.id
  # The broker's public address: HTTPS ingress terminates TLS, so clients speak MQTT over wss on 443 (#154).
  mosquitto_host = azurerm_container_app.mosquitto.ingress[0].fqdn
  # The API's internal address, reachable only inside the environment and served over HTTPS.
  api_url = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}

# Mosquitto with persistence on local, ephemeral storage: a broker restart within a session loses its queue (README:
# Broker persistence).
resource "azurerm_container_app" "mosquitto" {
  name                         = "fieldtrack-mosquitto"
  container_app_environment_id = azurerm_container_app_environment.demo.id
  resource_group_name          = data.azurerm_resource_group.demo.name
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [local.identity_id]
  }

  dynamic "secret" {
    for_each = toset(["mqtt-ingest-password", "mqtt-simulator-password"])
    content {
      name                = secret.key
      identity            = local.identity_id
      key_vault_secret_id = azurerm_key_vault_secret.demo[secret.key].versionless_id
    }
  }

  # HTTP ingress carries WebSockets; external TCP ingress would need a VNet. Request timeout 240 s (NOTES: clients ping
  # every 15 s or less).
  ingress {
    external_enabled = true
    target_port      = 8080
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "mosquitto"
      image  = var.mosquitto_image
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name        = "MQTT_INGEST_PASSWORD"
        secret_name = "mqtt-ingest-password"
      }
      env {
        name        = "MQTT_SIMULATOR_PASSWORD"
        secret_name = "mqtt-simulator-password"
      }
    }
  }
}

resource "azurerm_container_app" "api" {
  name                         = "fieldtrack-api"
  container_app_environment_id = azurerm_container_app_environment.demo.id
  resource_group_name          = data.azurerm_resource_group.demo.name
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [local.identity_id]
  }

  dynamic "secret" {
    for_each = toset(["db-connection-string", "auth-signing-key", "mqtt-ingest-password", "admin-temporary-password", "demo-password"])
    content {
      name                = secret.key
      identity            = local.identity_id
      key_vault_secret_id = azurerm_key_vault_secret.demo[secret.key].versionless_id
    }
  }

  # An ingestion key rather than a password, and already in state as an Application Insights attribute.
  secret {
    name  = "appinsights-connection-string"
    value = azurerm_application_insights.demo.connection_string
  }

  # Internal only: the dashboard proxies /api/ here and the simulator seeds through it.
  ingress {
    external_enabled = false
    target_port      = 8080
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "api"
      image  = var.api_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__FieldTrack"
        secret_name = "db-connection-string"
      }
      # No release pipeline runs migrations here; one replica means no two hosts race.
      env {
        name  = "Database__MigrateOnStartup"
        value = "true"
      }
      env {
        name  = "Broker__Host"
        value = local.mosquitto_host
      }
      env {
        name  = "Broker__Port"
        value = "443"
      }
      env {
        name  = "Broker__Transport"
        value = "WebSockets"
      }
      env {
        name  = "Broker__WebSocketPath"
        value = "/mqtt"
      }
      env {
        name  = "Broker__UseTls"
        value = "true"
      }
      env {
        name  = "Broker__Username"
        value = "ingest"
      }
      env {
        name        = "Broker__Password"
        secret_name = "mqtt-ingest-password"
      }
      env {
        name        = "Auth__SigningKey"
        secret_name = "auth-signing-key"
      }
      env {
        name  = "FIELDTRACK_ADMIN_EMAIL"
        value = var.admin_email
      }
      # Temporary: the first sign-in must replace it (README: After apply).
      env {
        name        = "FIELDTRACK_ADMIN_PASSWORD"
        secret_name = "admin-temporary-password"
      }
      env {
        name  = "FIELDTRACK_DEMO_EMAIL"
        value = var.demo_email
      }
      env {
        name        = "FIELDTRACK_DEMO_PASSWORD"
        secret_name = "demo-password"
      }
      env {
        name        = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        secret_name = "appinsights-connection-string"
      }
      env {
        name  = "AzureMonitor__SamplingRatio"
        value = tostring(var.app_insights_sampling_ratio)
      }
    }
  }

  depends_on = [
    # The first migration creates the PostGIS extension.
    azurerm_postgresql_flexible_server_configuration.extensions,
    azurerm_postgresql_flexible_server_firewall_rule.azure_services,
  ]
}

# The one public web address: nginx serves the Angular build and proxies /api/ (REST and the SignalR hub) to the API, so
# the browser sees one origin and needs no CORS (ADR 0006).
resource "azurerm_container_app" "dashboard" {
  name                         = "fieldtrack-dashboard"
  container_app_environment_id = azurerm_container_app_environment.demo.id
  resource_group_name          = data.azurerm_resource_group.demo.name
  revision_mode                = "Single"

  ingress {
    external_enabled = true
    target_port      = 80
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "dashboard"
      image  = var.dashboard_image
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "FIELDTRACK_API_URL"
        value = local.api_url
      }
    }
  }
}

# Seeds the demo fleet, Geofence and Alert Rules as the Admin, then publishes the demo Scenario at demo rate over wss.
# It fails and restarts until the Admin has replaced the temporary password (README: After apply).
resource "azurerm_container_app" "simulator" {
  name                         = "fieldtrack-simulator"
  container_app_environment_id = azurerm_container_app_environment.demo.id
  resource_group_name          = data.azurerm_resource_group.demo.name
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [local.identity_id]
  }

  dynamic "secret" {
    for_each = toset(["admin-password", "mqtt-simulator-password"])
    content {
      name                = secret.key
      identity            = local.identity_id
      key_vault_secret_id = azurerm_key_vault_secret.demo[secret.key].versionless_id
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "simulator"
      image  = var.simulator_image
      cpu    = 0.25
      memory = "0.5Gi"
      args = [
        "demo",
        "--fleet", "fleets/demo.json",
        "--scenario", "fleets/demo-scenario.json",
        "--rate", "5",
        "--api", local.api_url,
        "--mqtt-transport", "wss",
        "--mqtt-host", local.mosquitto_host,
        "--mqtt-username", "simulator",
        # The image's working directory may be read-only.
        "--state", "/tmp/simulator-state.json",
        "--manifest", "/tmp/manifest.json",
      ]

      env {
        name  = "FIELDTRACK_ADMIN_EMAIL"
        value = var.admin_email
      }
      env {
        name        = "FIELDTRACK_ADMIN_PASSWORD"
        secret_name = "admin-password"
      }
      env {
        name        = "FIELDTRACK_MQTT_PASSWORD"
        secret_name = "mqtt-simulator-password"
      }
    }
  }
}
