# Terraform bootstrap stack

Applied once from your laptop for the one demo session, then destroyed after the demo stack (see [Tear down](#tear-down-end-of-the-session)). It creates what the `demo` stack and its GitHub Actions workflows need before they can run (issue #155, deployment decision in #12, lifecycle in #51):

- **State storage:** resource group `rg-fieldtrack-bootstrap`, a Storage account (TLS 1.2, no public blob access, blob versioning, shared-key access off) and container `tfstate`. The demo stack's `azurerm` backend reaches it with `use_azuread_auth = true`.
- **Demo resource group:** `rg-fieldtrack-demo` (variable `demo_resource_group_name`). The demo stack deploys into it and must use the same name; it reads the group and never creates or deletes it. Destroying the demo stack leaves the group empty, which costs nothing.
- **GitHub identities:** two user-assigned managed identities with GitHub federated credentials, so workflows sign in with OIDC and no secret is stored. Managed identities instead of an Entra app registration, because the subscription sits in the university directory, where students may not register apps.
- **Budget alerts:** an email at $1 of usage, and again at $150 actual or forecast, before the $200 free credit runs out. The budget runs from 2026-11-01 to 2026-12-31 with a quarterly period, so the whole credit window (2026-11-18 to 2026-12-18) is one period.

This stack keeps its state in a local `terraform.tfstate` (it creates the remote backend, so it cannot use it). The file is gitignored; keep it until the teardown, because it is the only record of what was created.

## Who can sign in

| Identity | Federated credential | Subject | Used by |
|---|---|---|---|
| `id-fieldtrack-github-plan` | `github-pull-request` | `repo:Danie1Le@157658847/FieldTrack@1458260786:pull_request` | `terraform plan` on pull requests |
| `id-fieldtrack-github` | `github-main` | `repo:Danie1Le@157658847/FieldTrack@1458260786:ref:refs/heads/main` | manual apply / destroy runs on `main` |

GitHub issues tokens for this repository with the [immutable subject format](https://docs.github.com/en/actions/reference/security/oidc), which embeds the numeric owner and repository ids (variables `github_owner_id` and `github_repository_id`, public values). A credential without them fails with `AADSTS700213`.

No GitHub environment is used. A job that sets `environment:` gets the subject `repo:Danie1Le@157658847/FieldTrack@1458260786:environment:<name>` instead, which needs its own credential in `main.tf`.

Pull requests from forks never receive an OIDC token from GitHub. A pull request from a branch in this repository runs branch code, so it signs in only as the read-only plan identity. The apply identity trusts only `main`, and `main` is protected by a repository ruleset: changes land only through a pull request with every review thread resolved and `build-and-test`, `web` and `smoke` passing; force-push and deletion are blocked.

Roles of the plan identity (`id-fieldtrack-github-plan`):

- **Reader** on the subscription: refresh the demo stack's resources.
- **Storage Blob Data Reader** on the `tfstate` container: read state.

It cannot take the state lock (a blob lease needs write access), so plan jobs run `terraform plan -lock=false`. If a refresh needs a POST read that Reader does not grant (for example listing keys), also pass `-refresh=false`. Reader on state exposes whatever the demo stack stores in state, so keep passwords out of it (write-only `*_wo` arguments or ephemeral values).

Roles of the apply/destroy identity (`id-fieldtrack-github`):

- **Contributor** on the subscription: create and destroy the demo stack's resources. Subscription rather than the demo group, because the azurerm provider registers resource providers, a subscription-level action.
- **Storage Blob Data Contributor** on the state account: read and write state, take the state lock.
- **Role Based Access Control Administrator** on the demo resource group, with a condition: it may create and delete only *Key Vault Secrets User* and *Key Vault Secrets Officer* assignments, and only for service principals (managed identities). The demo stack needs this to let its apps read Key Vault. User Access Administrator is not needed, and the condition stops the identity from granting itself Owner or giving anything to a user.

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

3. Confirm the free credit pays for this subscription before the budget and the first apply (#51). In the Azure portal, **Cost Management + Billing**:
   - **Billing profile → Benefits → Azure credits** shows the $200 credit and its remaining balance.
   - **Billing profile → Products + services → All billing subscriptions** lists the subscription `az account show` selected, so it bills to that same billing profile.

   Use only first-party Azure services in the demo stack (no Marketplace), so the credit covers them. Never upgrade the account.

## Apply once

```powershell
cd infra/terraform/bootstrap
Copy-Item terraform.tfvars.example terraform.tfvars   # then edit: a globally unique account name and your email
terraform init
terraform plan -out bootstrap.tfplan
terraform apply bootstrap.tfplan
```

`terraform.tfvars`, `*.tfvars.json`, `terraform.tfstate*`, `.terraform/` and `*.tfplan` are gitignored. Never commit them: the repository is public.

## After apply

Copy the outputs into **GitHub → Settings → Secrets and variables → Actions → Variables** (repository variables, not secrets; they are ids, not credentials):

```powershell
terraform output
```

| Repository variable | Output |
|---|---|
| `AZURE_CLIENT_ID` | `AZURE_CLIENT_ID` (apply/destroy on `main`) |
| `AZURE_PLAN_CLIENT_ID` | `AZURE_PLAN_CLIENT_ID` (plan on pull requests) |
| `AZURE_TENANT_ID` | `AZURE_TENANT_ID` |
| `AZURE_SUBSCRIPTION_ID` | `AZURE_SUBSCRIPTION_ID` |
| `DEMO_RESOURCE_GROUP` | `DEMO_RESOURCE_GROUP` |
| `TF_STATE_RESOURCE_GROUP` | `TF_STATE_RESOURCE_GROUP` |
| `TF_STATE_STORAGE_ACCOUNT` | `TF_STATE_STORAGE_ACCOUNT` |
| `TF_STATE_CONTAINER` | `TF_STATE_CONTAINER` |

One value is not an output and goes under **Secrets** instead: the repository secret `DEMO_ADMIN_EMAIL`, the first Admin's email, which the demo stack's `admin_email` needs. Only Apply demo reads it (Destroy demo and the plan pass a placeholder). It is a sign-in name only (the API sends no mail), but half of the Admin's sign-in: anyone who knows it can lock the Admin out with wrong passwords. As a secret, GitHub masks it in this public repository's run logs, and `admin_email` is a sensitive Terraform variable, so plan and apply output hide it too.

Do not paste these values into files in the repository; workflows read them as `${{ vars.AZURE_CLIENT_ID }}` and so on.

The workflows in `.github/workflows` that use them (#157):

| Workflow | Runs | Identity |
|---|---|---|
| `images.yml` (Images) | after CI passes on a push to `main`: builds the four demo images and pushes them to GHCR | none (the job's `GITHUB_TOKEN`, `packages: write`) |
| `terraform-plan.yml` (Terraform plan) | pull requests touching `infra/`: `plan -lock=false -refresh=false` for both stacks; skipped for forks and while `AZURE_PLAN_CLIENT_ID` is unset. `plan-demo` also skips before the first Apply demo and while a session is applied, so it rarely runs (demo README: Checks) | plan |
| `demo-apply.yml` (Apply demo) | by hand on `main` | apply |
| `demo-destroy.yml` (Destroy demo) | by hand on `main` | apply |

The bootstrap plan starts from empty state (the real state is the local file), so it checks the configuration against the subscription rather than showing a diff. Apply demo and Destroy demo have separate concurrency groups (`demo-apply`, `demo-destroy`), so an Apply can never drop a waiting Destroy; the state blob's lease keeps them from changing the stack at once (demo README: Destroy).

GHCR makes a new package private. After the first Images run on `main`, open each of `fieldtrack-api`, `fieldtrack-dashboard`, `fieldtrack-mosquitto` and `fieldtrack-simulator` under your GitHub profile's **Packages**, then **Package settings → Change visibility → Public**: Container Apps pulls them without credentials, and Apply demo refuses to start while one cannot be pulled anonymously.

## Running the demo stack from the laptop

State has shared-key access off, so your own account also needs a data role to run the demo stack locally: assign yourself **Storage Blob Data Contributor** on the state account in the portal (Access control (IAM)), or run the demo stack only from GitHub Actions.

## Tear down (end of the session)

One session only (#51): apply the demo stack, record the results, destroy the demo stack, then destroy this stack, so the subscription holds no resources. The order matters: the demo stack's destroy needs this stack's identity and remote state.

1. Run the **Destroy demo** workflow on `main` and wait for it to succeed.
2. Confirm the demo resource group is empty:

   ```powershell
   az resource list --resource-group rg-fieldtrack-demo --output table
   ```

3. Destroy this stack from the same folder, with the local `terraform.tfstate`:

   ```powershell
   cd infra/terraform/bootstrap
   terraform destroy
   ```

   The azurerm provider refuses to delete a resource group that still holds resources, so a demo resource left behind stops this step instead of being deleted unseen. This destroy also removes the state account, the identities, the budget and the role assignments, which live at subscription scope outside both resource groups.

4. Delete the repository variables and the `DEMO_ADMIN_EMAIL` secret (GitHub → Settings → Secrets and variables → Actions), so the plan jobs skip instead of failing to sign in as an identity that no longer exists.

5. Confirm nothing is left:

   ```powershell
   az group list --output table
   az consumption budget list --output table
   ```

If the local state is lost, delete by hand: the resource groups `rg-fieldtrack-demo` and `rg-fieldtrack-bootstrap`, the subscription's **Access control (IAM)** role assignments for `id-fieldtrack-github` and `id-fieldtrack-github-plan`, and the budget `fieldtrack-credit` under **Cost Management → Budgets**.
