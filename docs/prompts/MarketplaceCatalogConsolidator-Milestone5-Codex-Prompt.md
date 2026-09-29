# Milestone 5 - HTTP Upload Handling and API Contract

Milestone 4 is accepted. Read these documents before changing code:

1. `MarketplaceCatalogConsolidator-Codex-Instructions.md`
2. `MarketplaceCatalogConsolidator-Solution-Design.md`
3. `MarketplaceCatalogConsolidator-Milestone4-Codex-Prompt.md`

Implement **Milestone 5 only**. Wire accepted HTTP uploads into the existing staging, persistence, serialized worker, and immutable report lifecycle. Do not build, run, inspect, publish, or verify a Docker image; that work is reserved for the final Azure deployment milestone.

## Endpoint

Implement and document this versioned endpoint:

```text
POST /api/v1/uploads
```

The endpoint accepts exactly one UTF-8 JSON file through `multipart/form-data`.

Required request inputs:

| Input | Requirement |
|---|---|
| File field | Exactly one file field named `file` |
| `Idempotency-Key` | Required RFC 4122 UUID v4 header |
| `X-Api-Key` | Required API-key header |

The endpoint returns `202 Accepted` for a newly accepted upload with:

- `uploadId`;
- initial/current status;
- file name and SHA-256;
- a `Location` header pointing to `/api/v1/uploads/{uploadId}/status`.

The status endpoint itself is out of scope for this milestone; returning a future URL is required.

## Authentication

- Read the upload API key from configuration/environment only; do not add it to source control, local settings, logs, reports, query strings, or Swagger examples.
- Use constant-time comparison.
- Missing or invalid key returns a consistent `401 Unauthorized` error envelope without revealing why it failed.
- Do not introduce Entra, OAuth, or any other authentication provider in this milestone.

## File validation and safe staging

- Require `multipart/form-data`; reject unsupported media types with `415`.
- Reject missing, empty, or multiple uploaded files with a clear `400` error envelope.
- The file must be strictly smaller than **500,000 bytes**. A file of exactly 500,000 bytes must return `413 Payload Too Large`.
- Enforce the limit both from declared content length when available and while streaming. Never trust a client-provided file extension or content type alone.
- Validate the payload as UTF-8 JSON array input using the existing source parser before creating a queued upload. Invalid JSON returns `400` and creates no durable upload/staging artifact.
- Keep the original filename only as display metadata. Generate all physical paths from server-generated upload IDs; do not use client paths or filenames as path segments.
- Stream the accepted file to the existing staging abstraction, calculate SHA-256, atomically persist the staging file, and then persist/queue the `Upload` record using the existing application workflow.
- On failures/cancellation before acceptance, clean up temporary staging files and avoid orphaned upload records.

## Idempotency and concurrent requests

- Validate `Idempotency-Key` as UUID v4 before any durable acceptance.
- The first valid request creates one staged file and one upload record.
- A repeat with the same key and same SHA-256 returns the original upload identity and current status without staging a second file or queueing a second workflow.
- A repeat with the same key and a different SHA-256 returns `409 Conflict` and leaves the original upload unchanged.
- Concurrent requests using the same idempotency key must converge on exactly one durable upload. Use database constraints/transactions, not process memory alone.
- Multiple distinct accepted uploads may be queued concurrently; the existing single worker remains the only consolidator.

## API and OpenAPI requirements

- Keep all routes versioned under `/api/v1`.
- Use the established error envelope: `traceId`, `code`, and `message`.
- Create a new trace ID or adopt the existing correlation mechanism for every request; propagate it into the accepted upload record.
- Add accurate OpenAPI/Swagger documentation for multipart input, required headers, `202`, `400`, `401`, `409`, `413`, and `415` responses.
- The Swagger UI remains public, but no secret value may be prefilled or committed.
- Do not add list, status, report-download, catalog-query, health, or readiness endpoint mappings in this milestone.

## Required tests

Use application/API integration tests with disposable data roots and SQLite databases. At minimum, prove:

1. Valid multipart JSON with a valid API key creates exactly one queued upload, durable staged file, and worker-visible workflow record, then returns `202` plus `Location`.
2. Missing/invalid API key returns `401` and leaves no staging/upload artifact.
3. Missing/non-v4 idempotency key returns `400` and leaves no staging/upload artifact.
4. Invalid JSON or non-array JSON returns `400` and leaves no staging/upload artifact.
5. Unsupported content type returns `415`.
6. Empty/multiple files return `400`.
7. Exactly 500,000 bytes returns `413`; streaming limit enforcement rejects an oversized upload even without a trustworthy content length.
8. Same idempotency key plus same content returns the same upload without a duplicate record/file.
9. Same idempotency key plus different content returns `409` and preserves the original upload.
10. Concurrent same-key requests result in exactly one durable upload.
11. Client-supplied filenames cannot escape the configured staging root.

Do not use arbitrary timing/sleeps as correctness assertions. Ensure tests do not mutate the supplied starter database or a production data directory.

## Scope boundaries

- Do not add list, status, report-download, catalog-query, health, or readiness endpoints in this milestone.
- Do not change core consolidation, normalization, migration, worker, or report-finalization policies except for the narrow application wiring necessary to accept a staged upload.
- Do not add rate limiting, cloud resources, image publication, GitHub operations, or a web UI.
- Do not build or run Docker images. If the repository verifier includes Docker steps, skip only those steps and report their intentional deferral.

## Completion protocol

1. Inspect the current host configuration, staging service, upload persistence, workflow signal, and integration-test setup.
2. State a concise implementation plan before editing.
3. Implement only Milestone 5.
4. Run the repository verifier, release build, tests, formatting checks, and API/OpenAPI validation, omitting Docker-only verification.
5. Report changed files, exact commands run, test results, representative API evidence, and limitations.
6. Stop at the Milestone 5 handoff and propose the next milestone without proceeding automatically.
