# Marketplace Catalog Consolidator — Milestone 9 Codex Prompt

## Goal

Add one deliberately small public utility endpoint that generates and returns a fresh RFC 4122 UUID version 4.

## Branch and delivery rules

- Start from updated `main`; confirm the worktree is clean.
- Create branch `feat/random-uuid-endpoint`.
- Do not commit directly to `main`, merge, enable auto-merge, build/run Docker, publish a container, or perform Azure work.
- Complete the change, verify it locally, commit, push the branch, and open one pull request. Stop for review and human merge.

## API contract

Add exactly this public endpoint:

```http
GET /api/v2/random-uuid
```

- No authentication, request body, query parameters, storage access, or database access.
- Apply the existing public-read rate-limit policy and existing security/global-error middleware.
- Return `200 OK` and JSON shaped exactly as:

```json
{ "uuid": "xxxxxxxx-xxxx-4xxx-[89ab]xxx-xxxxxxxxxxxx" }
```

- Generate the value server-side using `Guid.NewGuid()` and serialize its canonical lowercase `D` form. Do not use `Random`, hard-coded data, UUIDv1/v7, external services, or a new dependency.
- Name the endpoint clearly, such as `GenerateRandomUuid`.
- In Swagger/OpenAPI, document it as “Generate a random UUID v4”; include only the `200`, `429`, and standard safe `500` response metadata. It must have no request-body schema and no API-key parameter.
- Do not alter existing `/api/v1/*` routes or the existing authenticated `POST /api/v2/reset-database` contract.

## Tests

Add focused API integration coverage that proves:

1. The endpoint is public and returns `200` without `X-Api-Key`.
2. The `uuid` property parses as a GUID, has version 4, and uses RFC 4122 variant bits.
3. Two requests return different values.
4. It has no storage/database side effects (use the existing test fixture’s isolated storage root).
5. Live `/openapi/v1.json` contains `GET /api/v2/random-uuid`, documents `200` and `429`, and has neither `requestBody` nor `X-Api-Key`.

Keep all existing tests and endpoint behavior unchanged. Follow existing project coding style and API endpoint organization; do not create a new architectural layer for this stateless utility.

## Documentation and verification

Update `README.md` and `MarketplaceCatalogConsolidator.Api.http` with a concise example. Update solution-design documentation only if it lists every public endpoint.

Run:

```powershell
dotnet format MarketplaceCatalogConsolidator.sln --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
gitleaks dir . --config .gitleaks.toml --redact --no-banner
gitleaks git . --config .gitleaks.toml --redact --no-banner
```

In the handoff, provide the PR URL, branch, commit SHA, changed files, exact test totals/results, and confirmation that no Docker or Azure operation occurred. Stop without merging.
