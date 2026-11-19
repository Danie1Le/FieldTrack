variable "location" {
  description = "Azure region for the bootstrap resources."
  type        = string
  default     = "westus2"
}

variable "state_storage_account_name" {
  description = "Globally unique name of the Storage account that holds Terraform state (3-24 lowercase letters and digits)."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]{3,24}$", var.state_storage_account_name))
    error_message = "Use 3-24 lowercase letters and digits."
  }
}

variable "demo_resource_group_name" {
  description = "Resource group the demo stack deploys into. Created and owned here; the demo stack reads it and must use the same name."
  type        = string
  default     = "rg-fieldtrack-demo"
}

variable "budget_alert_email" {
  description = "Address the budget alerts are sent to. Set it in terraform.tfvars, which is gitignored."
  type        = string
}

variable "github_owner_id" {
  description = "Numeric id of the GitHub account Danie1Le (public: api.github.com/users/Danie1Le). Part of the immutable OIDC subject."
  type        = number
  default     = 157658847
}

variable "github_repository_id" {
  description = "Numeric id of the GitHub repository Danie1Le/FieldTrack (public: api.github.com/repos/Danie1Le/FieldTrack). Part of the immutable OIDC subject."
  type        = number
  default     = 1458260786
}
