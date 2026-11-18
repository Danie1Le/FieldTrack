# Non-secret ids for GitHub repository variables (README: "After apply"). Workflows sign in with OIDC,
# so no secret is ever copied out of this stack.

output "AZURE_CLIENT_ID" {
  description = "Client id of the deploy identity (azure/login client-id)."
  value       = azurerm_user_assigned_identity.github.client_id
}

output "AZURE_TENANT_ID" {
  description = "Directory (tenant) id the subscription belongs to."
  value       = data.azurerm_subscription.current.tenant_id
}

output "AZURE_SUBSCRIPTION_ID" {
  description = "Subscription the demo stack deploys into."
  value       = data.azurerm_subscription.current.subscription_id
}

output "TF_STATE_RESOURCE_GROUP" {
  description = "Resource group of the state Storage account (azurerm backend resource_group_name)."
  value       = azurerm_resource_group.bootstrap.name
}

output "TF_STATE_STORAGE_ACCOUNT" {
  description = "State Storage account (azurerm backend storage_account_name)."
  value       = azurerm_storage_account.state.name
}

output "TF_STATE_CONTAINER" {
  description = "State container (azurerm backend container_name)."
  value       = azurerm_storage_container.state.name
}
