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
