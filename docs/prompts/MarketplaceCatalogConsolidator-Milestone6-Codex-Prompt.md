# Milestone 6 - Public Read, Status, Report, Catalog, and Health API

Milestone 5 is accepted. Read these documents before changing code:

1. `MarketplaceCatalogConsolidator-Codex-Instructions.md`
2. `MarketplaceCatalogConsolidator-Solution-Design.md`
3. `MarketplaceCatalogConsolidator-Milestone5-Codex-Prompt.md`

Implement **Milestone 6 only**. Add the public read/status API surface over existing durable data. Keep `POST /api/v1/uploads` API-key protected. Do not build, run, inspect, publish, or verify a Docker image; image work is reserved for the final Azure deployment milestone.

## Endpoints

Implement and document these versioned public endpoints:

| Endpoint | Requirement |
|---|---|
| `GET /api/v1/uploads` | Paginated upload attempts, newest first |
| `GET /api/v1/uploads/{uploadId}/status` | One upload plus persisted item outcomes |
| `GET /api/v1/uploads/{uploadId}/report` | Immutable JSON report download |
| `GET /api/v1/catalog` | Paginated canonical catalog query |
| `GET /api/v1/health` | Process liveness |
| `GET /api/v1/ready` | Storage/database readiness |

All read endpoints are public. Do not require or expose the upload API key on these endpoints.

## Upload-attempt list

`GET /api/v1/uploads` supports:

```text
?status=&page=1&pageSize=25
```

- Order by upload start timestamp descending, then stable upload ID descending.
- Validate pagination; reject invalid values with the standard error envelope.
- Cap page size at a documented safe maximum.
- Each item includes upload ID, filename, status, started/completed timestamps, summary counts, and whether the final report is available.
- Do not expose idempotency keys, staged file paths, API keys, internal exceptions, or sensitive diagnostics.

## Upload status

`GET /api/v1/uploads/{uploadId}/status` returns:

- upload ID, filename, state, timestamps, trace ID, summary counts, and report availability;
- any non-sensitive failure code/message;
- persisted per-item outcomes in stable source-index order;
- raw source values, cleaned values when present, status, action taken, and canonical product ID when available.

Support item pagination:

```text
?page=1&pageSize=50
```

Return `404` with the standard error envelope for an unknown or invalid upload ID.

## Report download

`GET /api/v1/uploads/{uploadId}/report`:

- Resolve the report through durable upload metadata, never through caller-supplied paths.
- Return `application/json` for a verified immutable final report.
- Return `404` for an unknown upload.
- Return `409` with code `report_not_available` while the upload is non-terminal or finalization is pending.
- If database metadata refers to a missing/invalid report, fail safely with a non-sensitive `500` error and structured log evidence; do not serve another file or generate a replacement from this endpoint.
- Do not expose staging files, temporary report files, or filesystem paths.

## Catalog query

Implement:

```text
GET /api/v1/catalog?category=&brand=&name=&sellerName=&page=1&pageSize=25
```

Rules:

- All filters are optional, case-insensitive, and whitespace-tolerant.
- Product filtering uses safe parameterized SQLite queries only.
- `category`, `brand`, and `name` filter canonical `Product` values.
- `sellerName` selects products offered by the matching seller.
- Return canonical product ID/name/brand/category and seller offers. When `sellerName` is supplied, return only matching seller offers; otherwise return all offers for each returned product.
- Order products deterministically by product ID ascending unless a documented better stable ordering is selected.
- Return pagination metadata and never duplicate a product because it has multiple sellers.

## Health and readiness

`GET /api/v1/health` is a lightweight liveness response: it must not perform database or filesystem work.

`GET /api/v1/ready` verifies the application can:

- open the configured SQLite working database;
- enforce or verify foreign-key support as appropriate;
- access the configured staging and report roots for safe write operations without creating durable business artifacts.

Return `200` when ready and `503` with a non-sensitive error envelope when not ready. Do not leak paths, secrets, or raw exceptions.

## API and OpenAPI requirements

- Preserve URL-segment versioning under `/api/v1`.
- Add accurate public OpenAPI documentation for every endpoint, query parameter, success response, and scoped error response.
- Keep Swagger public and usable without an API key for these read endpoints.
- Reuse the established `traceId`, `code`, and `message` error envelope.
- Ensure the existing upload endpoint's `Location` target is now live and correct.

## Required tests

Add API/integration tests using disposable SQLite databases and report roots. At minimum, prove:

1. Upload listing returns newest-first, stable pagination, filtering by status, and no sensitive fields.
2. Upload status returns persisted item outcomes in source-index order with correct summary/counts and `404` for unknown IDs.
3. Report download returns exactly the immutable report bytes/content type for a completed upload, `409` before availability, and never permits path traversal or staging-file access.
4. Catalog filters work independently and together for category, brand, product name, and seller name.
5. Catalog pagination is stable and a multi-seller product appears once.
6. Catalog queries use the expected seller-offer response behavior when seller filtering is present.
7. Health does not depend on a working database; readiness returns `200` when dependencies are available and `503` when injected dependency checks fail.
8. Public read endpoints do not require an API key; `POST /api/v1/uploads` remains protected.
9. OpenAPI documents all newly mapped read endpoints and the upload `Location` is retrievable after acceptance.

Avoid time-based assertions. Tests must not mutate the supplied starter database or write into a production data directory.

## Scope boundaries

- Do not change upload acceptance, core matching, worker, report-generation, or migration policy except for narrow query/health abstractions required to expose existing data.
- Do not add rate limiting, cloud resources, image publication, GitHub operations, or a web UI.
- Do not build or run Docker images. If the repository verifier includes Docker steps, skip only those steps and report their intentional deferral.

## Completion protocol

1. Inspect the existing API host, persistence query capabilities, report metadata, and test fixtures.
2. State a concise implementation plan before editing.
3. Implement only Milestone 6.
4. Run the repository verifier, release build, tests, formatting checks, and OpenAPI/API validation, omitting Docker-only verification.
5. Report changed files, exact commands run, test results, representative endpoint evidence, and limitations.
6. Stop at the Milestone 6 handoff and propose the next milestone without proceeding automatically.
