output "dashboard_url" {
  description = "The demo's public address: the dashboard, with the API behind /api/."
  value       = "https://${azurerm_container_app.dashboard.ingress[0].fqdn}"
}

output "mqtt_url" {
  description = "The broker for MQTT over WebSockets with TLS (users ingest and simulator, passwords in Key Vault)."
  value       = "wss://${local.mosquitto_host}/mqtt"
}

output "key_vault_name" {
  description = "Key Vault holding the generated passwords (README: After apply)."
  value       = azurerm_key_vault.demo.name
}
