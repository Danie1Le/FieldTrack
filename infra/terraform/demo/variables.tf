variable "resource_group_name" {
  description = "Resource group the bootstrap stack created for the demo (its DEMO_RESOURCE_GROUP output). Read, never created or deleted here."
  type        = string
  default     = "rg-fieldtrack-demo"
}

variable "admin_email" {
  description = "Email of the first Admin, whose temporary password the simulator replaces with admin-password at its first start. Set it in terraform.tfvars (gitignored) or the workflow, never in a committed file."
  type        = string
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
