# Milestone 7 - Operational Hardening and GitHub Pull Request Creation

Milestone 6 is accepted. Read these documents before changing code:

1. `MarketplaceCatalogConsolidator-Codex-Instructions.md`
2. `MarketplaceCatalogConsolidator-Solution-Design.md`
3. `MarketplaceCatalogConsolidator-Milestone6-Codex-Prompt.md`

Implement **Milestone 7 only**. At the end, create the public GitHub repository and open its first pull request. Do not merge any pull request. Do not build, run, inspect, publish, or verify a Docker image; image work is reserved for the final Azure deployment milestone.

## Goal

Harden the public API for safe demonstration, complete repository governance/documentation, create the public repository `MarketplaceCatalogConsolidator`, and open the first PR into `main` containing the project. The user performs every merge.

## Operational hardening

### Configuration and secrets

- Validate required runtime configuration at startup without printing secret values.
- Keep the upload API key in environment/App Service configuration only. Never add a real key to source, tests, examples, local settings, logs, reports, or OpenAPI.
- Provide a clearly named development-only configuration convention with a harmless placeholder, documented as non-production.
- Ensure logs redact/avoid request headers, API keys, raw uploaded contents, staged paths, and stack traces returned to callers.

### Rate limiting and resilience

- Add endpoint-specific ASP.NET Core rate limiting.
- Protect `POST /api/v1/uploads` with a conservative fixed-window policy. Return `429` using the established error envelope and `Retry-After` when available.
- Apply a separate, more generous policy to public read endpoints.
- Use a deterministic partition strategy appropriate for the API: do not trust arbitrary client-supplied forwarded-IP headers.
- Preserve the existing serialized SQLite workflow; rate limiting must not alter idempotency or worker correctness.
- Add a global exception handler that logs structured, non-sensitive diagnostics and returns the standard error envelope.

### HTTP safety

- Add appropriate non-breaking security response headers that remain compatible with public Swagger/OpenAPI.
- Enforce request-body limits consistently with the existing strictly-less-than-500,000-byte upload rule.
- Do not add a web UI or external authentication provider.

## Repository quality and governance

Add or refine only the repository artifacts needed for a clear public engineering project:

- README: concise project purpose, architecture, setup, API/Swagger validation, API-key configuration, persistence/recovery behavior, report behavior, limitations, and deployment intent.
- `CONTRIBUTING.md`: branch -> PR -> review -> user merge workflow.
- `SECURITY.md`: responsible disclosure guidance and secret-handling expectations.
- `.github/pull_request_template.md`: summary, tests, security, migration/data impact, documentation, and reviewer checklist.
- CI: build, test, formatting, and secret scanning. Do not add Docker image build/push jobs.
- A suitable secret-scanning configuration/workflow. It must scan the repository without requiring secrets and must not introduce Python tooling.
- Verify `.gitignore` excludes runtime databases, reports, staged uploads, local secrets, build output, and editor/system artifacts.

Do not add company names to generated repository content.

## Tests

Add focused tests proving:

1. Upload rate limiting returns `429` with the standard error envelope and does not create extra staging/upload artifacts.
2. Read endpoints use their distinct public rate-limit policy.
3. API-key values and secret-like headers are not reflected in error responses or structured log output under testable abstractions.
4. Global unhandled exceptions return the standard non-sensitive error envelope with a trace ID.
5. Existing upload idempotency, report, catalog, health, readiness, and public-read behavior remain green.
6. CI/secret-scanning configuration is syntactically valid through available local validation; if the scanner binary is unavailable, document the exact CI validation path rather than installing unrelated tooling.

## GitHub repository and pull request

Only perform these external GitHub operations after all local verification passes.

1. Check `gh auth status` and confirm it identifies the intended authenticated account. If not authenticated or account identity is unclear, stop and report the blocker; do not use another account or ask for credentials in chat.
2. Create a **public** repository named exactly `MarketplaceCatalogConsolidator` with an initial remote README so `main` exists.
3. Preserve the local project working tree. Use a safe temporary clone/worktree of the new remote when needed to avoid overwriting local files while establishing the remote base.
4. Create and push a feature branch named `chore/operational-hardening` that contains the complete project and Milestone 7 changes.
5. Create a pull request from `chore/operational-hardening` into `main` with a concise title and a body summarizing architecture, verification, security posture, and deferred Docker/Azure deployment.
6. Do **not** merge the PR, enable auto-merge, change branch-protection settings, delete branches, or push project code directly to `main`.
7. Report the repository URL, PR URL, branch name, and verification evidence. The user will review and merge the PR.

For all future work, follow this invariant:

```text
feature branch -> pull request -> user review -> user merge to main
```

Never directly merge or push project changes to `main`.

## Scope boundaries

- Do not change business-level consolidation/matching/report semantics except for hardening integration.
- Do not add cloud resources, deployment configuration, image publication, GitHub release publication, or a web UI.
- Do not build or run Docker images. If the repository verifier includes Docker steps, skip only those Docker-specific steps and report their intentional deferral.

## Completion protocol

1. Inspect current API middleware/configuration, CI, repository status, and GitHub CLI authentication before editing.
2. State a concise implementation plan before changing files.
3. Implement only Milestone 7.
4. Run the repository verifier, release build, tests, formatting checks, OpenAPI/API validation, and available secret-scanning validation, omitting Docker-only verification.
5. Only after passing verification, create the public repository and its initial PR following the above rules.
6. Report changed files, exact commands run, test results, repository URL, PR URL, and limitations.
7. Stop at the Milestone 7 handoff. Do not merge the PR or proceed automatically.
