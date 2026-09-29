# Milestone 12 — Immutable Container Deployment to Azure App Service

## Objective

Deploy the frozen Marketplace Catalog Consolidator application from an immutable, **public GHCR image** to **Azure App Service for Linux** in **West Central US**, using persistent App Service `/home` storage for the existing SQLite runtime data.

This is a deployment-only milestone. Preserve the project exactly as it stands after Milestone 11.

## Non-negotiable change boundary

- Milestone 11 (`feat/static-catalog-ui`, PR #19) is frozen. Do not modify, delete, rename, reformat, regenerate, or move any existing tracked file, source file, test, artifact, workflow, documentation, or configuration.
- Before starting, verify that PR #19 has been merged into `main`, the local checkout is on updated `main`, and the working tree is clean. If any condition is false, stop and report it.
- **Only add new files.** The final diff must contain additions only. If an existing file must be edited for any reason, stop and ask Samuel for explicit permission before changing it.
- Do not alter the supplied SQLite database, JSON fixture, runtime behavior, API contract, static UI, or test suite.
- Do not delete Azure resources, GitHub packages, container images, tags, workflows, or local files. Do not use destructive Git commands.

## Mandatory feasibility gate — run before provisioning

The requested target is Azure App Service Linux **Free (F1)** in `westcentralus` with a custom Linux container. Current Azure documentation has conflicting signals about whether Free can host Linux custom containers. Resolve this from the active Azure subscription and region before creating any resource.

1. Confirm the intended subscription and signed-in Azure identity without printing secrets.
2. Perform a read-only, authoritative capability check for `westcentralus` and the Linux F1 custom-container combination. Record the commands and the relevant output in a new deployment document.
3. If F1 Linux custom containers are unavailable, unsupported, quota-blocked, or require a paid plan:
   - do **not** create a resource group, plan, web app, image, GHCR package, or deployment;
   - do **not** substitute B1 or any other paid tier;
   - report the exact evidence and ask Samuel whether to authorize a specific alternative.
4. Continue only when the F1 target is positively confirmed in the active subscription and region.

This is fail-closed. Cost, tier, or region changes require an explicit new user decision.

## Deliverables — additions only

Add only the following kinds of files when useful; use names that fit the repository style:

- `infra/azure/` deployment scripts and a non-secret example configuration file;
- `.github/workflows/` manually dispatched workflow(s) for build, public GHCR publishing, and Azure deployment;
- `docs/` deployment runbook and evidence document;
- optional deployment-focused tests only if they can be added without modifying existing files.

Do not edit the README, Dockerfile, appsettings files, project files, source, tests, artifacts, or existing workflows.

## Image requirements

1. Build from the existing, frozen Dockerfile only after the feasibility gate passes. Docker operations are allowed in this milestone.
2. Publish to the repository owner’s GHCR namespace with a lowercase package name.
3. Use a full Git commit SHA tag for traceability. Do not use or update `latest`, floating tags, or mutable deployment references.
4. Obtain the pushed image digest and configure App Service with the digest-pinned reference:

   ```text
   ghcr.io/<owner>/<package>@sha256:<digest>
   ```

5. Make the GHCR package public and verify anonymous pull/manifest access before configuring App Service. If package visibility cannot be made public with available authority, stop and report the required GitHub setting; never workaround it with credentials embedded in App Service.
6. Record only the image repository, full commit SHA tag, and digest in documentation. Never record tokens, passwords, publish profiles, API keys, connection strings, or raw Azure credentials.

## Azure deployment requirements

After the gate passes, create only the minimum required resources in `westcentralus`:

- one Linux App Service plan at F1;
- one App Service web app with a globally unique, documented name;
- HTTPS-only enabled;
- one instance (do not add paid add-ons or unrelated Azure services).

Use the public digest-pinned GHCR image. Configure the existing application through App Service settings only:

- `ASPNETCORE_ENVIRONMENT=Production`
- `ASPNETCORE_HTTP_PORTS=8080`
- `WEBSITES_PORT=8080` if App Service requires it for the container listener
- `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true`
- the application’s existing storage-root setting pointed to `/home/data`
- a production API key supplied securely at execution time (prompt securely or read a pre-existing local environment variable; never place it in a file, command history, workflow log, repository, or final report)

Do not enable the development placeholder API-key setting in Azure. Do not mount storage anywhere other than the existing intended `/home` path. Do not assume `/home` persistence: verify it after deployment.

Use the locally authenticated operator workflow in `infra/azure/Deploy-AppServiceF1.ps1`. Azure authentication must use the operator's interactive Azure CLI session; GHCR authentication must use Docker's interactive login prompt; GitHub package visibility is managed through the authenticated GitHub CLI session. Do not store an Azure credential, GHCR token, API key, publish profile, or client secret in the repository, command line, or deployment logs.

## Deployment sequence and pull-request discipline

1. Create a feature branch, add the deployment-only files, and open a pull request. Do not merge it.
2. Run locally safe checks for the added scripts/workflow syntax. Build the container only after the F1 gate permits it.
3. Hand off the PR for Samuel to review and merge.
4. **Only after Samuel confirms the deployment PR is merged**, resume from updated `main` and execute `infra/azure/Deploy-AppServiceF1.ps1`. Do not deploy an image built from a PR branch.
5. Make no other repository changes during the execution phase.

If a live Azure deployment must be split into a follow-up turn after merge, stop with a crisp checklist of the one-time inputs/permissions needed and wait.

## Required validation after deployment

Validate the live URL without exposing credentials:

- the root static UI loads;
- `/swagger/` loads;
- `/api/v1/health` returns healthy;
- `/api/v1/ready` returns ready;
- the configured running image is the exact recorded GHCR digest;
- `/home/data` is the active runtime storage root;
- a safe upload/status/report round trip works with the configured API key;
- after one controlled App Service restart, the accepted upload/status/report remain available, proving persistence across restart.

Use a newly generated idempotency UUID for any validation upload. Do not reset the database or remove validation data afterward. Do not expose the API key in terminal output, screenshots, docs, PR text, or logs.

## Verification and handoff

Before the PR handoff, run the established repository verifier and the secret scan. After the deployment, report:

- pull request URL, branch, commit, and whether it is merged;
- Azure region, plan tier, app name, and public application URL;
- GHCR image repository, commit-SHA tag, and immutable digest;
- evidence of public GHCR access, live health/readiness, digest pinning, and `/home` persistence;
- exact commands/tests run and their outcomes;
- every preflight finding, limitation, and any action Samuel must take.

Never claim success if the F1 feasibility gate fails, a credential is missing, or persistence was not demonstrated.
