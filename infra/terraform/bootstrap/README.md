# Terraform bootstrap stack

Applied once from your laptop and kept. It creates what the `demo` stack and its GitHub Actions workflows need before they can run (issue #155, deployment decision in #12):

- **State storage:** resource group `rg-fieldtrack-bootstrap`, a Storage account (TLS 1.2, no public blob access, blob versioning, shared-key access off) and container `tfstate`. The demo stack's `azurerm` backend reaches it with `use_azuread_auth = true`.
- **Deploy identity:** user-assigned managed identity `id-fieldtrack-github` with GitHub federated credentials, so workflows sign in with OIDC and no secret is stored. A managed identity instead of an Entra app registration, because the subscription sits in the university directory, where students may not register apps.
- **Budget alerts:** an email at $1 of usage, and again at $150 actual or forecast, before the $200 free credit runs out.

This stack keeps its state in a local `terraform.tfstate` (it creates the remote backend, so it cannot use it). The file is gitignored; keep it, because it is the only record of what was created.

## Who can sign in as the deploy identity

| Federated credential | Subject | Used by |
|---|---|---|
| `github-pull-request` | `repo:Danie1Le/FieldTrack:pull_request` | `terraform plan` on pull requests |
| `github-main` | `repo:Danie1Le/FieldTrack:ref:refs/heads/main` | manual apply / destroy runs on `main` |

No GitHub environment is used. A job that sets `environment:` gets the subject `repo:Danie1Le/FieldTrack:environment:<name>` instead, which needs its own credential in `main.tf`.

Pull requests from forks never receive an OIDC token from GitHub, so only branches pushed to this repository can sign in. Anyone who can push a branch can run code under this identity, so keep write access to the repository to yourself.

Roles granted to the identity:

- **Contributor** on the subscription: the demo stack creates and destroys its own resource group every session.
- **Storage Blob Data Contributor** on the state account: read and write state, take the state lock.
- **Role Based Access Control Administrator** on the subscription, with a condition: it may create and delete only *Key Vault Secrets User* and *Key Vault Secrets Officer* assignments, and only for service principals (managed identities). The demo stack needs this to let its apps read Key Vault. User Access Administrator is not needed, and the condition stops the identity from granting itself Owner or giving anything to a user.

## Prerequisites

1. Azure CLI and Terraform 1.17:

   ```powershell
   winget install Microsoft.AzureCLI
   winget install Hashicorp.Terraform
   ```

2. Sign in with the account that owns the subscription (you need **Owner**, to create role assignments):

   ```powershell
   az login
   az account show --query "{name:name, id:id, tenant:tenantId}"
   ```

   If `az login` lists several subscriptions, pick the free account's: `az account set --subscription "<name>"`.

3. Check the remaining credit before applying: Azure portal, **Cost Management + Billing → Benefits** (Azure credits). Never upgrade the account.

## Apply once

```powershell
cd infra/terraform/bootstrap
Copy-Item terraform.tfvars.example terraform.tfvars   # then edit: a globally unique account name and your email
terraform init
terraform plan -out bootstrap.tfplan
terraform apply bootstrap.tfplan
```

`terraform.tfvars`, `terraform.tfstate*`, `.terraform/` and `*.tfplan` are gitignored. Never commit them: the repository is public.

## After apply

Copy the outputs into **GitHub → Settings → Secrets and variables → Actions → Variables** (repository variables, not secrets; they are ids, not credentials):

```powershell
terraform output
```

| Repository variable | Output |
|---|---|
| `AZURE_CLIENT_ID` | `AZURE_CLIENT_ID` |
| `AZURE_TENANT_ID` | `AZURE_TENANT_ID` |
| `AZURE_SUBSCRIPTION_ID` | `AZURE_SUBSCRIPTION_ID` |
| `TF_STATE_RESOURCE_GROUP` | `TF_STATE_RESOURCE_GROUP` |
| `TF_STATE_STORAGE_ACCOUNT` | `TF_STATE_STORAGE_ACCOUNT` |
| `TF_STATE_CONTAINER` | `TF_STATE_CONTAINER` |

Do not paste these values into files in the repository; workflows read them as `${{ vars.AZURE_CLIENT_ID }}` and so on.

## Running the demo stack from the laptop

State has shared-key access off, so your own account also needs a data role to run the demo stack locally: assign yourself **Storage Blob Data Contributor** on the state account in the portal (Access control (IAM)), or run the demo stack only from GitHub Actions.
