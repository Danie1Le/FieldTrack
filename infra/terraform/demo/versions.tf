terraform {
  required_version = ">= 1.17.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "5.14.0"
    }
    # Ephemeral random_password: generated passwords never reach state (bootstrap README: the plan identity reads it).
    random = {
      source  = "hashicorp/random"
      version = "3.9.1"
    }
    time = {
      source  = "hashicorp/time"
      version = "0.14.2"
    }
  }

  # State lives in the bootstrap stack's Storage account, reached with Entra ID (shared keys are off there). The
  # account, container and resource group come from `terraform init -backend-config=...` (README: Apply), so no id
  # is committed. CI validates with `init -backend=false`.
  backend "azurerm" {
    key              = "demo.tfstate"
    use_azuread_auth = true
  }
}

provider "azurerm" {
  features {}

  # The state account has shared-key access off; reach any Storage data plane with Entra ID too.
  storage_use_azuread = true
}
