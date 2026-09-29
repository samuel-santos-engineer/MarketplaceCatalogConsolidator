# Marketplace Catalog Consolidator - Codex Implementation Instructions

## 1. Your role and operating mode

You are the primary implementation engineer for **Marketplace Catalog Consolidator**. Deliver a high-rigor, public GitHub project for the Catalog Consolidation take-home assessment.

Work incrementally. At the beginning of each milestone, inspect the current repository state. Before changing code, state the exact milestone goal and acceptance criteria. After each milestone, run the relevant verification, report the evidence, and stop for review unless explicitly asked to continue.

Do not invent or silently relax requirements. When requirements conflict or a decision is genuinely missing, explain the conflict and ask before proceeding. Preserve unrelated user changes. Never commit secrets, API keys, generated SQLite data, or reports.

## 2. Authoritative project identity

- Product name: **Marketplace Catalog Consolidator**
- GitHub repository: `MarketplaceCatalogConsolidator`
- Target framework: **.NET 10**
- Deployment: Docker image on Azure App Service Linux F1, West Central US
- Persistence: SQLite, staging files, and reports under `/home/data` in Azure
- No web UI in this delivery. Swagger/OpenAPI is the public validation interface. A future UI will consume the API.

Read `MarketplaceCatalogConsolidator-Solution-Design.md` before implementation. It is the primary design authority. This file defines the execution constraints and immediate work plan.

## 3. Provided assessment assets

- The assessment includes a starter SQLite database at `artifacts/catalog.db`.
- The database initially contains `Product` and `SellerProduct` tables, 975 products, and no seller-product links.
- Seller input is a JSON array with this contract:

```json
{
  "Id": "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d",
  "SellerName": "MegaStore",
  "Name": "Smartphone Galaxy S23",
  "Brand": "Samsung",
  "Category": "Electronics"
}
```

- The source JSON is available from:
  `https://engineering-hiring-process.s3.us-east-1.amazonaws.com/ProductEntry.json`

Treat the starter database as an immutable source asset. The running database is a copied working database and is migrated forward safely.

## 4. Non-negotiable functional requirements

All API endpoints use URL-segment versioning and start at `v1`:


| Endpoint                                | Requirement                                                     |
| ----------------------------------------- | ----------------------------------------------------------------- |
| `POST /api/v1/uploads`                  | Accept one JSON file and queue consolidation                    |
| `GET /api/v1/uploads`                   | List upload attempts, paginated, newest first                   |
| `GET /api/v1/uploads/{uploadId}/status` | Return upload state and per-item outcomes                       |
| `GET /api/v1/uploads/{uploadId}/report` | Return the immutable JSON report file                           |
| `GET /api/v1/catalog`                   | Query catalog by category, brand, product name, and seller name |
| `GET /api/v1/health`                    | Liveness check                                                  |
| `GET /api/v1/ready`                     | Verify SQLite and persistent directories are writable           |

`POST /api/v1/uploads` requirements:

- `multipart/form-data` file upload.
- UTF-8 JSON only; strictly smaller than **500,000 bytes**. Exactly 500,000 bytes is rejected with `413 Payload Too Large`.
- Require `Idempotency-Key` as an RFC 4122 UUID v4.
- Require `X-Api-Key`; configure its value through environment/App Service settings only.
- Return `202 Accepted`, an `uploadId`, initial `Queued` state, and a status `Location` header.
- Same idempotency key and same file hash returns the original upload. Same key with a different file returns `409 Conflict`.

## 5. Consolidation policy

For every source entry:

1. Validate required fields and GUID-shaped source `Id`.
2. Apply the agreed suspicious SQL-like input rejection policy. Even rejected inputs must never be interpolated into SQL.
3. Trim field values and collapse internal whitespace.
4. Normalize case and remove accents for comparison.
5. Clean category alias `Photo` to `Photography`.
6. Resolve brand and category against values known in the canonical `Product` catalog. Unknown values become `NULL`.
7. Strictly match **normalized Brand + Name + Category** only when all three normalized components exist.
8. On a match, link the seller to the existing product.
9. Without a match, create a new canonical product using cleaned values and link the seller.
10. Detect same-seller repeated source entries by `(SellerName, SellerProductId)`.

Outcome semantics are mandatory:


| Status     | Meaning                                                                                                   |
| ------------ | ----------------------------------------------------------------------------------------------------------- |
| `Approved` | Input was valid without cleaning; product was created or seller was linked to an existing product         |
| `Cleaned`  | One or more transformations were applied; product was created or seller was linked to an existing product |
| `Rejected` | The item cannot safely be accepted due to validation, source-ID conflict, or the agreed security policy   |

For a legitimate duplicate from another seller, report `Approved` when unchanged or `Cleaned` when normalized. Do not reject it merely because the canonical product already exists.

Each action report must be human-readable. For cleaning, include field name, before value, and after value. Example: `Name: \"Smartphone  Galaxy S23\" -> \"Smartphone Galaxy S23\"; linked to Product 2.`

## 6. Persistence, queue, and recovery

- Keep the supplied `Product` and `SellerProduct` concepts; migrate `SellerProduct.SellerProductId` from integer to text.
- Use parameterized SQLite commands for every query and command.
- Add persisted `Upload` and `UploadItem` audit data required by the list and status endpoints.
- Persist an incoming file to `/home/data/uploads/{uploadId}.json` before marking it `Queued`.
- Use one serialized worker: many clients may upload at once, but only one consolidation operation writes SQLite at a time.
- Valid items process in independent transactions so one rejected record does not undo accepted records.
- Persist item progress; after restart, return interrupted work to `Queued` and continue without duplicating committed items.
- Generate exactly one immutable report for every terminal upload, using temp-file then atomic rename:

```text
/home/data/reports/{uploadId}.json
```

- Only mark an upload completed after its report exists. Delete the staged source JSON after report creation. Never delete reports for queued, processing, or failed uploads.
- Return `507 Insufficient Storage` if safe staging/report storage is unavailable.

## 7. Security and observability

- Swagger/OpenAPI and read endpoints are public for reviewers.
- Only upload is API-key protected. Do not store the key in source control, logs, reports, query strings, or Swagger examples.
- Compare API keys in constant time.
- Enforce endpoint-appropriate rate limits: strict on uploads, more generous on public reads.
- Use HTTPS only in deployed environments.
- Run the Docker image as a non-root user.
- Return a consistent error envelope with `traceId`, `code`, and `message`.
- Emit structured logs containing `traceId`, `uploadId`, and source index where available.

## 8. Required project quality

- Clean, maintainable solution structure with API, application, domain, infrastructure, and test boundaries.
- Explicit dependency direction: API -> Application -> Domain; Infrastructure implements Application-facing ports.
- Nullable reference types, warnings treated seriously, clear naming, cancellation tokens, and safe resource disposal.
- Dockerfile, `.dockerignore`, `.gitignore`, editor configuration, pinned SDK configuration, and GitHub Actions CI.
- Do not commit runtime SQLite databases, reports, staging files, `appsettings.Development.json` secrets, or environment files.
- README must explain architecture, assumptions, local execution, environment variables, Swagger validation, Docker, Azure deployment, and known limitations.

## 9. Verification requirements

Create focused unit tests and SQLite integration tests that prove:

- Starter data migration preserves 975 products.
- `Photo` cleans to `Photography`.
- Whitespace and accent normalization work.
- Cross-seller duplicates create seller links without duplicate products.
- SQL-like input is rejected and never executes SQL.
- Idempotency retries return the same upload; reused key plus new file yields `409`.
- Same seller/source-ID conflict is rejected.
- Concurrent uploads queue safely.
- Restart recovery resumes without duplicate processing.
- Every terminal upload creates one valid report.

Run build, tests, formatting, and any repository verification script before claiming a milestone complete. If the local environment lacks an essential tool, report the exact blocked validation and provide the command to run locally.

## 10. Immediate work plan

Start with **Milestone 0 - Repository foundation and design validation** only.

1. Inspect the repository, starter database, and source JSON; report the findings that affect implementation.
2. Create the .NET 10 solution skeleton and project boundaries.
3. Establish build/test/format/verify scripts, CI, Docker baseline, safe configuration, and documentation structure.
4. Add no functional upload/consolidation endpoint in this milestone.
5. Validate the foundation and stop with a concise milestone handoff: changed files, commands run, results, and proposed Milestone 1 scope.

Do not deploy to Azure, create cloud resources, publish images, or push to GitHub unless the user explicitly asks.
