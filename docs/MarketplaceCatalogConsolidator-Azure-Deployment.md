# Immutable Azure App Service Deployment

## Frozen deployment target

| Setting | Value |
| --- | --- |
| Azure region | `westcentralus` (West Central US) |
| App Service plan | Linux `F1`, one instance |
| Resource group | `rg-marketplace-catalog-wcus` |
| Plan name | `plan-marketplace-catalog-f1-wcus` |
| Web app name | `marketplace-catalog-consolidator-ss-wcus` |
| Public URL after deployment | `https://marketplace-catalog-consolidator-ss-wcus.azurewebsites.net` |
| GHCR repository | `ghcr.io/samuel-santos-engineer/marketplace-catalog-consolidator` |
| Runtime storage | `/home/data` with App Service storage enabled |

The image tag is the full merge commit SHA and the deployed reference is digest-pinned. No `latest` or other floating tag is created or used.

## Mandatory feasibility evidence

The gate was run on 2026-09-29 before any Azure resource, image, or package was created.

- Active identity: `sabsfilho@gmail.com` (`user`).
- Active subscription: `Learn Azure subscription`, ID `7f5eabb5-80f5-4369-88be-b14a6de763f1`, state `Enabled`, default `true`.
- `Microsoft.Web` provider state: `Registered`; its `serverFarms` locations include `West Central US`.
- `az appservice list-locations --sku F1 --linux-workers-enabled` returned `West Central US`.
- An ARM `/validate` request against existing resource group `rg-aiq-r112-wp03-wcus-5ec325382770` validated a Linux `F1` server farm and `app,linux,container` web app in `westcentralus` with provisioning state `Succeeded`.
- Azure's `Microsoft.Web/checknameavailability` endpoint returned `nameAvailable: true` for `marketplace-catalog-consolidator-ss-wcus`.
- The validation request created zero resources. No resource group, plan, web app, GHCR package, or image existed as a result of the gate.

Re-run the non-mutating gate when the subscription changes:

```powershell
./infra/azure/Test-AppServiceF1Feasibility.ps1 `
  -ValidationResourceGroup rg-aiq-r112-wp03-wcus-5ec325382770
```

The script prints only non-secret identity, subscription, and capability results. It keeps its Azure access token in memory and never prints or writes it.

## Local operator authentication

The deployment is run locally from a clean, up-to-date `main` checkout. No Azure or GHCR credential is stored in the repository or GitHub Actions.

1. Sign in interactively with `az login`, select the intended subscription with `az account set --subscription <subscription-id>`, and verify it with `az account show`.
2. Sign in to GitHub CLI as the repository owner using `gh auth login` and verify the account with `gh api user --jq .login`. The account needs permission to publish the package and change its visibility to public.
3. Ensure Docker Desktop is running. The deployment script invokes `docker login ghcr.io` interactively; enter a GitHub credential only into Docker's prompt, never into the script, shell command, repository, or chat.
4. Run `./infra/azure/Deploy-AppServiceF1.ps1` from the repository. The production API key is requested using a hidden PowerShell prompt and is sent only to the new App Service settings resource over the authenticated Azure CLI session.

The local operator's Azure identity must have permission to create a resource group and its App Service plan, web app, and settings in the selected subscription. No service principal secret, publish profile, PAT in a file, or GitHub Actions secret is used.

## Review, merge, and local deployment

1. Review and merge the local-deployment replacement PR. Do not deploy from its feature branch.
2. Update the checkout to `main` and run `./infra/azure/Deploy-AppServiceF1.ps1`.
3. Confirm the displayed identity/subscription and type the app-specific deployment confirmation. The script repeats the read-only Linux F1/custom-container gate, checks name availability, and refuses existing target resources before building or publishing.
4. Authenticate to GHCR interactively when Docker prompts. The script builds only the full merge-commit SHA tag, reads the digest, makes the package public using the authenticated GitHub CLI session, and proves anonymous manifest access before creating Azure resources.
5. It creates the Linux F1 plan and HTTPS-only app in `westcentralus`, applies production settings including `Catalog__StorageRoot=/home/data`, configures the digest-pinned public image, and validates the UI, Swagger, health, readiness, upload/status/report, configuration, and report persistence after one controlled restart.

If public-package visibility cannot be changed, F1 validation fails, the name is unavailable, or any validation fails, stop. Do not substitute a paid tier or another region. The script refuses existing Azure resources; it does not delete partially created resources on failure, so inspect and report any partial provisioning before rerunning.

## Post-deployment evidence to retain

Record from the successful workflow without copying secrets:

- workflow run URL and merge commit SHA;
- full-SHA image tag and `sha256` digest;
- anonymous GHCR pull result;
- Azure resource group, F1 plan, app name, region, and public URL;
- configured `linuxFxVersion` digest match and `Catalog__StorageRoot=/home/data` assertion;
- health/readiness responses and the validation upload ID;
- successful status/report reads before and after restart.

The validation upload is intentionally retained as persistence evidence. Do not reset the database or remove it after deployment.

## Updating the existing F1 app

After merging a code change, update a clean local `main` checkout to match `origin/main` and run:

```powershell
./infra/azure/Deploy-AppServiceF1.ps1 -UpdateExisting
```

Confirm the displayed Azure identity and subscription, then type the app-specific `UPDATE-F1-...` confirmation when prompted. This mode refuses an absent or unexpected app, plan, region, image repository, HTTPS setting, or persistent-storage configuration. It builds and publishes an image tagged with the full merge commit SHA, resolves its digest, and patches only the existing app's container image reference. It does not recreate resources, reset `/home/data`, rotate `Security__ApiKey`, overwrite application settings, or submit a new validation upload.

Before updating, it reads an existing report if available. After the app becomes ready, it checks the full app-settings collection for changes and verifies health, upload count, and that existing report's identity and summary. If validation fails after the image change, it attempts to restore the previous immutable image reference and reports that live health and settings still need inspection. The original create-only invocation without `-UpdateExisting` remains available for a new installation.

The local `.worktrees/` directory is ignored by Git; existing worktrees are preserved, not deleted or deployed. The clean-tree and up-to-date-main checks still apply to both deployment modes.
