# Issue #155: pull requests run branch code, so the identity they sign in as must hold only read roles.
# Runs offline against a mocked provider; nothing reaches Azure.

mock_provider "azurerm" {
  mock_data "azurerm_subscription" {
    defaults = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000000"
      subscription_id = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_resource "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-bootstrap/providers/Microsoft.Storage/storageAccounts/fieldtracktfstate0000"
    }
  }

  mock_resource "azurerm_storage_container" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-bootstrap/providers/Microsoft.Storage/storageAccounts/fieldtracktfstate0000/blobServices/default/containers/tfstate"
    }
  }

  mock_resource "azurerm_resource_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-demo"
    }
  }
}

override_resource {
  target = azurerm_user_assigned_identity.github
  values = {
    id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-bootstrap/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-fieldtrack-github"
    principal_id = "11111111-1111-1111-1111-111111111111"
  }
}

override_resource {
  target = azurerm_user_assigned_identity.github_plan
  values = {
    id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-fieldtrack-bootstrap/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-fieldtrack-github-plan"
    principal_id = "22222222-2222-2222-2222-222222222222"
  }
}

variables {
  state_storage_account_name = "fieldtracktfstate0000"
  budget_alert_email         = "alerts@example.com"
}

run "pull_requests_sign_in_only_as_the_read_only_plan_identity" {
  assert {
    condition     = azurerm_federated_identity_credential.github_pull_request.user_assigned_identity_id == azurerm_user_assigned_identity.github_plan.id
    error_message = "The pull_request credential must belong to the plan identity."
  }

  assert {
    condition     = azurerm_federated_identity_credential.github_main.subject == "repo:Danie1Le/FieldTrack:ref:refs/heads/main"
    error_message = "The apply identity must trust only the main branch."
  }

  assert {
    condition = alltrue([
      for ra in [
        azurerm_role_assignment.github_contributor,
        azurerm_role_assignment.github_state,
        azurerm_role_assignment.github_grant_key_vault_roles,
      ] : ra.principal_id == azurerm_user_assigned_identity.github.principal_id
    ])
    error_message = "Write roles must go to the apply identity only."
  }

  assert {
    condition = (
      azurerm_role_assignment.github_plan_reader.role_definition_name == "Reader" &&
      azurerm_role_assignment.github_plan_state.role_definition_name == "Storage Blob Data Reader" &&
      azurerm_role_assignment.github_plan_reader.principal_id == azurerm_user_assigned_identity.github_plan.principal_id &&
      azurerm_role_assignment.github_plan_state.principal_id == azurerm_user_assigned_identity.github_plan.principal_id
    )
    error_message = "The plan identity must hold only read roles."
  }

  assert {
    condition     = azurerm_user_assigned_identity.github_plan.principal_id != azurerm_user_assigned_identity.github.principal_id
    error_message = "Plan and apply must be separate identities."
  }
}
