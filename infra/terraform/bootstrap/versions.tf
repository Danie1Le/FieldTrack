terraform {
  required_version = ">= 1.17.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "5.14.0"
    }
  }

  # No backend block: this stack creates the state storage, so it keeps its state locally (gitignored).
}

provider "azurerm" {
  features {}

  # The state account has shared-key access off, so the provider must reach its data plane with Entra ID.
  storage_use_azuread = true
}
