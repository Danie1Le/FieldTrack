data "azurerm_subscription" "current" {}

locals {
  # Issue #155: only this repository's pull requests (plan) and default branch (apply/destroy) may sign in.
  # The demo workflows use no GitHub environment: a job with `environment:` gets the subject
  # `repo:<repo>:environment:<name>` instead and would need its own credential here.
  github_subjects = {
    pull-request = "repo:Danie1Le/FieldTrack:pull_request"
    main         = "repo:Danie1Le/FieldTrack:ref:refs/heads/main"
  }

  # Built-in Key Vault data roles the demo stack may grant to managed identities (its apps read secrets,
  # the deploy identity writes them). Role definition ids are the same in every tenant.
  grantable_role_ids = [
    "4633458b-17de-408a-b874-0445c86b69e6", # Key Vault Secrets User
    "b86a8fe4-44ce-4948-aee5-eccb2c155cd7", # Key Vault Secrets Officer
  ]
  grantable_roles = join(", ", local.grantable_role_ids)
}

resource "azurerm_resource_group" "bootstrap" {
  name     = "rg-fieldtrack-bootstrap"
  location = var.location
}

resource "azurerm_storage_account" "state" {
  name                            = var.state_storage_account_name
  resource_group_name             = azurerm_resource_group.bootstrap.name
  location                        = azurerm_resource_group.bootstrap.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  # Entra ID only: the demo stack's backend uses use_azuread_auth, so no account key is ever handed out.
  shared_access_key_enabled = false

  blob_properties {
    # Every state write keeps the previous version, so a bad apply can be rolled back.
    versioning_enabled = true
  }
}

resource "azurerm_storage_container" "state" {
  name               = "tfstate"
  storage_account_id = azurerm_storage_account.state.id
}

# A managed identity rather than an Entra app registration: the subscription sits in the university
# directory, where students may not register apps. Federated credentials mean no stored secret.
resource "azurerm_user_assigned_identity" "github" {
  name                = "id-fieldtrack-github"
  resource_group_name = azurerm_resource_group.bootstrap.name
  location            = azurerm_resource_group.bootstrap.location
}

resource "azurerm_federated_identity_credential" "github" {
  for_each = local.github_subjects

  name                      = "github-${each.key}"
  user_assigned_identity_id = azurerm_user_assigned_identity.github.id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = "https://token.actions.githubusercontent.com"
  subject                   = each.value
}

# The demo stack creates and destroys its own resource group each session, so it needs the subscription.
resource "azurerm_role_assignment" "github_contributor" {
  scope                = data.azurerm_subscription.current.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.github.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "github_state" {
  scope                = azurerm_storage_account.state.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.github.principal_id
  principal_type       = "ServicePrincipal"
}

# Contributor cannot write role assignments. Instead of User Access Administrator, this lets the identity
# create and delete only Key Vault data-role assignments, and only for service principals (managed
# identities), so it can never grant Owner or anything to a user or group.
resource "azurerm_role_assignment" "github_grant_key_vault_roles" {
  scope                = data.azurerm_subscription.current.id
  role_definition_name = "Role Based Access Control Administrator"
  principal_id         = azurerm_user_assigned_identity.github.principal_id
  principal_type       = "ServicePrincipal"
  condition_version    = "2.0"
  condition            = <<-EOT
    (
     (
      !(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})
     )
     OR
     (
      @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${local.grantable_roles}}
      AND
      @Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}
     )
    )
    AND
    (
     (
      !(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})
     )
     OR
     (
      @Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${local.grantable_roles}}
      AND
      @Resource[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}
     )
    )
  EOT
}

# Thresholds are percents of the amount; a $100 budget makes them read as dollars (1% is $1). Budgets count
# usage before credits, so these fire while the $200 free credit is still paying. Annual, not monthly:
# the credit window spans two calendar months and a monthly budget would reset halfway through it.
resource "azurerm_consumption_budget_subscription" "credit" {
  name            = "fieldtrack-credit"
  subscription_id = data.azurerm_subscription.current.id
  amount          = 100
  time_grain      = "Annually"

  time_period {
    # The free credit runs 2026-11-18 to 2026-12-18; a budget must start on the first of a month.
    start_date = "2026-11-01T00:00:00Z"
  }

  notification {
    operator       = "GreaterThanOrEqualTo"
    threshold      = 1
    threshold_type = "Actual"
    contact_emails = [var.budget_alert_email]
  }

  notification {
    operator       = "GreaterThanOrEqualTo"
    threshold      = 150
    threshold_type = "Actual"
    contact_emails = [var.budget_alert_email]
  }

  notification {
    operator       = "GreaterThanOrEqualTo"
    threshold      = 150
    threshold_type = "Forecasted"
    contact_emails = [var.budget_alert_email]
  }
}
