variable "resource_group_name" {
  description = "Resource group the bootstrap stack created for the demo (its DEMO_RESOURCE_GROUP output). Read, never created or deleted here."
  type        = string
  default     = "rg-fieldtrack-demo"
}

variable "location" {
  description = "Azure region for every demo resource. Independent of the resource group's location, which does not constrain its resources."
  type        = string
  # Free-account subscriptions get no PostgreSQL Flexible Server offer in some regions (westus2, eastus, eastus2 and
  # southcentralus returned none: "ParameterOutOfRange ... 'Version' should be in: []"). Check a region before
  # changing this: `az postgres flexible-server list-skus --location <region>` must list server versions and Standard_B1ms.
  default = "westus3"
}

variable "admin_email" {
  description = "Email of the first Admin, whose temporary password the simulator replaces with admin-password at its first start. Set it in terraform.tfvars (gitignored) or the workflow, never in a committed file."
  type        = string
  # Half of the Admin's sign-in, so plan and apply output in public run logs never show it (#169).
  sensitive = true
}

variable "demo_email" {
  description = "Email of the Demo Operator; the login page shows it with its password (ADR 0002)."
  type        = string
  default     = "demo@fieldtrack.example"
}

variable "deployer_principal_type" {
  description = "Principal type of whoever runs apply: ServicePrincipal for the GitHub identity (the only type its role-granting condition allows), User when you apply from the laptop."
  type        = string
  default     = "ServicePrincipal"

  validation {
    condition     = contains(["ServicePrincipal", "User"], var.deployer_principal_type)
    error_message = "Use ServicePrincipal or User."
  }
}

# Images from public GHCR (ADR 0006), published by the image workflow (#157). Pass an immutable tag or digest per session.
# Keep these four equal to the images.yml matrix and the image list in demo-apply.yml.
variable "api_image" {
  description = "API image, ingest worker inside (src/FieldTrack.Api/Dockerfile)."
  type        = string
  default     = "ghcr.io/danie1le/fieldtrack-api:main"
}

variable "dashboard_image" {
  description = "Dashboard image: nginx serving the Angular build and proxying /api/ to FIELDTRACK_API_URL (web/Dockerfile)."
  type        = string
  default     = "ghcr.io/danie1le/fieldtrack-dashboard:main"
}

variable "mosquitto_image" {
  description = "Broker image: Mosquitto with a WebSockets listener and password auth (docker/mosquitto/cloud/Dockerfile)."
  type        = string
  default     = "ghcr.io/danie1le/fieldtrack-mosquitto:main"
}

variable "simulator_image" {
  description = "Simulator image: its entrypoint runs the simulator CLI with the fleets folder in its working directory (src/FieldTrack.Simulator)."
  type        = string
  default     = "ghcr.io/danie1le/fieldtrack-simulator:main"
}

variable "log_daily_cap_gb" {
  description = "Daily ingestion cap, in GB, on the Log Analytics workspace and Application Insights (ADR 0006: daily cap on). Ingestion stops for the rest of the day once reached."
  type        = number
  default     = 0.5
}

variable "app_insights_sampling_ratio" {
  description = "Fixed share of traces the API sends to Application Insights (AzureMonitor:SamplingRatio, #158)."
  type        = number
  default     = 0.1
}
