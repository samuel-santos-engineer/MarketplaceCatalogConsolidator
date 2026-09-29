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

## One-time OIDC and secret setup

The manual workflow requires these GitHub Actions secrets:

- `AZURE_CLIENT_ID`: application/client ID for an Entra application or user-assigned managed identity.
- `AZURE_TENANT_ID`: tenant ID used by the active subscription.
- `AZURE_SUBSCRIPTION_ID`: target subscription ID.
- `PRODUCTION_API_KEY`: strong application upload key, generated and stored as a secret.

Configure a federated identity credential for this repository's `main` branch with subject:

```text
repo:samuel-santos-engineer/MarketplaceCatalogConsolidator:ref:refs/heads/main
```

Grant the federated principal only the role needed to create the named resource group and its App Service resources. Do not add a client secret, publish profile, personal access token, registry password, or API key to the repository. The workflow uses GitHub's short-lived OIDC token for Azure and `GITHUB_TOKEN` for GHCR.

## Review, merge, and dispatch

1. Review and merge the deployment PR. Do not dispatch from a PR branch.
2. Confirm the four secret names above exist in repository Actions settings.
3. Open **Actions → Deploy immutable container to Azure App Service → Run workflow** on `main`.
4. Keep the documented names and set **Confirm deployment must remain Linux F1 in West Central US** to `true`.
5. The workflow refuses pre-existing target resources, rechecks Linux F1 region availability, builds the frozen Dockerfile, pushes only the full-SHA tag, makes the package public, and proves an anonymous pull before creating Azure resources.
6. It deploys `ghcr.io/samuel-santos-engineer/marketplace-catalog-consolidator@sha256:<digest>`, enables HTTPS-only and `/home` storage, then validates UI, Swagger, health, readiness, upload/status/report, digest pinning, storage configuration, and report persistence after one controlled restart.

If public-package visibility cannot be changed, OIDC/secrets are absent, F1 validation fails, the name becomes unavailable, or any validation fails, the workflow stops. Do not substitute a paid tier, another region, registry credentials, or weaker Azure authentication.

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
