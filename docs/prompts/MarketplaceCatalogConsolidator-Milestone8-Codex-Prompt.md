# Marketplace Catalog Consolidator — Milestone 8 Codex Prompt

## Goal

Implement a **lab-only, authenticated database reset** endpoint that restores the running application to its clean, application-ready starter state.

The endpoint is intentionally available for this public demonstration project so its workflow can be repeatedly shown from a known baseline. It must not weaken the existing upload authentication or allow an accidental reset.

## Starting point and branch discipline

- Start from the current, merged `main` branch and first confirm the working tree is clean.
- Create a new feature branch named `feat/lab-database-reset`.
- Do **not** commit directly to `main`, merge, push a direct change to `main`, or enable auto-merge.
- At the end, commit the completed work, push the feature branch, and open one pull request. Stop for human review and merge.
- Keep all existing API behavior and contracts working.
- Do not perform Docker build, run, inspect, publish, Azure provisioning, deployment, or GitHub release work in this milestone.

## Endpoint contract

Add only this new endpoint:

```http
POST /api/v2/reset-database
```

It must:

- Require the existing `X-Api-Key` authentication mechanism. It is never anonymous and must use the same Development placeholder convention already implemented for local demonstrations.
- Require a second explicit request header: `X-Reset-Confirmation: RESET_DATABASE` (case-sensitive exact value). Missing or wrong values return the established safe error envelope with `400` and code `reset_not_confirmed`.
- Have no request body and reject an unexpected non-empty body with `400 invalid_request_body` before mutating state.
- Use the existing write/mutation rate-limit policy (or a dedicated equally strict reset policy). It must not use the public-read rate-limit bucket.
- Return `200 OK` only after reset and reinitialization are complete. Return a small JSON response containing `resetAtUtc`, `productCount`, and `sellerProductCount`; the expected baseline is 975 products and zero seller links.
- Describe this as a **lab reset** in OpenAPI/Swagger, including both required headers, all success/error statuses, and the destructive consequence. Do not put a real key or secret in OpenAPI examples.

There must be no unversioned, `v1`, GET, or anonymous reset route.

## Required reset semantics

“Original state” means the immutable source `artifacts/catalog.db` data is copied again into a fresh **working** database and then the normal forward migrations are reapplied. It does **not** mean bypassing the application schema migrations.

After a successful reset:

- the repository’s immutable `artifacts/catalog.db` is unchanged;
- the working database contains the starter catalog (975 `Product` rows) and no seller-product links;
- upload records, upload-item outcomes, idempotency records, generated reports, and staged upload files from previous demonstrations no longer exist;
- normal startup/bootstrap/migration invariants still hold and `/api/v1/ready` is healthy;
- the service remains running and can immediately accept a new `POST /api/v1/uploads` request.

Do not expose direct filesystem paths, database paths, exception messages, internal lock names, or secrets in the response or logs.

## Concurrency, durability, and safety

Study the existing persisted worker, data-root lock, SQLite claim logic, upload acceptance service, and bootstrapper before implementation. Extend the design rather than adding a competing ad-hoc lock.

- Reset must obtain an exclusive application-wide maintenance/workflow gate before touching storage. The worker, upload acceptance, report finalization, and reset must cooperate with that gate so no SQLite write, staged file write, report write, or migration can interleave with reset.
- An in-flight worker must reach a safe boundary before reset proceeds. It is acceptable to wait briefly using the existing cancellation/timeout conventions; do not terminate a transaction halfway through.
- While reset holds the gate, a competing upload or reset must fail safely and predictably (for example `409 reset_in_progress`) or wait and then operate against the clean state. Choose one documented behavior, implement it consistently, and test it under true overlap. Do not silently lose an accepted upload.
- Clean the working database, SQLite sidecar files, staging, and reports only while the exclusive gate is held. Preserve the root directory and any lock artifact needed by the holder; never delete the starter database.
- Reuse the existing atomic/bootstrap/migration abstractions where possible. If reset initialization fails, do not report success; leave readiness unhealthy and log a safe diagnostic. The next startup/reset must be able to recover.
- The endpoint is naturally state-idempotent: a second completed reset produces the same clean baseline. Do not reuse upload idempotency records because reset removes them.
- Use parameterized SQL everywhere and preserve all existing security headers, body limits, global error behavior, structured logs, and rate limits.

## Architecture

- Add a clearly named application port/use case (for example `ILabDatabaseResetService`) and place orchestration outside the HTTP endpoint.
- Keep endpoint code limited to authentication, header/body validation, invocation, and response mapping.
- Infrastructure owns filesystem/SQLite reset mechanics; it must not leak infrastructure details into API contracts.
- Register dependencies in the composition root and keep the existing dependency direction intact.
- Do not introduce a Python environment, Python tooling, new cloud dependencies, or a second database provider.

## Tests

Add focused unit/integration tests. Use isolated temporary storage roots; never mutate the repository starter database during tests.

At minimum prove:

1. Missing/invalid API key is `401` and does not reset anything.
2. Missing/wrong confirmation header is `400 reset_not_confirmed` and does not reset anything.
3. A non-empty body is rejected without mutation.
4. The documented `POST /api/v2/reset-database` contract appears in live OpenAPI; no unintended reset routes exist.
5. After a real accepted/processed upload creates seller links, upload history, a staged input or report as applicable, reset returns `200`, restores 975 products and zero seller links, removes prior upload/report artifacts, and leaves readiness healthy.
6. A new upload can be accepted and processed after reset.
7. Repeating reset yields the same clean baseline.
8. A real concurrent reset/upload (and, if practical, reset/reset) cannot corrupt SQLite or leave partial files. Assert the selected documented contention response/behavior.
9. The immutable starter `artifacts/catalog.db` checksum or byte contents remain unchanged.

Keep the existing full test suite green. Update or add contract tests without weakening prior assertions.

## Documentation and handoff

Update `README.md`, both solution-design copies, `MarketplaceCatalogConsolidator.Api.http`, and any relevant operational/security documentation:

- explain that reset is lab-only and destructive;
- show the local request using the public Development placeholder only in Development;
- explicitly state that production deployment should disable/remove this endpoint or require an intentionally separate production decision;
- document the confirmation header, response, contention behavior, and how local reset differs from deleting a storage folder;
- retain the existing rule: no actual secrets are committed.

Run and report:

```powershell
dotnet format MarketplaceCatalogConsolidator.sln --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
gitleaks dir . --config .gitleaks.toml --redact --no-banner
gitleaks git . --config .gitleaks.toml --redact --no-banner
```

Then create the pull request and provide its URL, branch, commit SHA, changed files, concise behavior summary, exact verification results, and any remaining limitation. Stop there for review; do not merge the PR.
