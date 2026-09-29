# Marketplace Catalog Consolidator

Marketplace Catalog Consolidator is a .NET 10 API for consolidating seller product catalogs into a canonical SQLite catalog. The project is being delivered incrementally against [the solution design](MarketplaceCatalogConsolidator-Solution-Design.md). Milestone 7 includes authenticated uploads, public read APIs and Swagger, operational hardening, and repository governance. Docker/Azure work is reserved for the final deployment milestone.

## Architecture

```text
API (composition root) -> Application -> Domain
API -> Infrastructure (Application ports implemented here)
UnitTests -> Domain + Application
IntegrationTests -> Infrastructure
```

The API project is the composition root and may reference Infrastructure for dependency registration. Application contains use cases and provider-neutral ports, Domain contains business rules and entities, and Infrastructure owns SQLite and file-system adapters. Unit and integration tests are kept in separate projects. The intended runtime uses one API instance and one serialized consolidation worker, with the working database, upload staging, and immutable reports persisted under `/home/data`.

## Current status

The solution targets `net10.0` and includes API, Application, Domain, Infrastructure, UnitTests, and IntegrationTests projects. The API exposes the seven versioned routes documented below. Startup copies the starter database if needed, applies migrations, recovers interrupted uploads, cleans stale report temp files, and resumes pending report finalizations before running the host. One hosted consolidation worker polls at a bounded interval and uses a data-root file lock plus an atomic SQLite claim to serialize uploads across loops and service instances sharing that persistent root. The production item processor parses staged UTF-8 JSON, validates and cleans source entries, resolves canonical brands/categories, strictly matches products, writes seller offers, and commits each item outcome atomically with its catalog changes. Terminal outcomes are persisted only after an immutable report is published and verified.

The root `catalog.db` is an immutable assessment input and must remain unchanged. Bootstrap copies it to a separate working database before migration. The supplied database has 975 `Product` rows and no `SellerProduct` links; migration preserves legacy seller rows and changes `SellerProduct.SellerProductId` to `TEXT NOT NULL`. For seller rows from older schemas, newly required audit fields unavailable in the source are marked with a deterministic `legacy:<row-id>` fingerprint and the Unix epoch timestamp. Migration history is stored in `SchemaMigration`, and product identity keys are normalized for matching. `ProductEntry.json` contains 269 entries.

## Prerequisites

- .NET SDK 10.0.103 baseline (pinned by `global.json`, with .NET 10 feature-band roll-forward enabled)
- Gitleaks 8.30.1 for local secret scanning; CI downloads the pinned native binary without secrets
- Docker is needed only for the final deployment milestone

## Local development

Build and test the solution:

```sh
dotnet build MarketplaceCatalogConsolidator.sln
dotnet test MarketplaceCatalogConsolidator.sln
```

Startup requires a non-empty `Security__ApiKey` supplied through the runtime environment. Production should use a unique high-entropy value. Missing credentials, a production development-placeholder, an unavailable starter database, or a working root that would overwrite the starter fail startup with a generic configuration message.

For a local demonstration only, this explicitly enabled Development convention uses the public placeholder `development-only-not-a-secret`:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Development__UsePlaceholderApiKey = 'true'
dotnet run --project src/MarketplaceCatalogConsolidator.Api --no-launch-profile --urls http://localhost:5254
```

[.env.development.example](.env.development.example) documents the convention; .NET does not load that file automatically. Paste the public placeholder into the upload header only for local Development. The convention and placeholder are rejected outside Development. For other environments, supply `Security__ApiKey` through protected environment/App Service settings, not committed files. Explicit runtime keys override the Development placeholder.

With the runtime environment configured, run the API host:

```sh
dotnet run --project src/MarketplaceCatalogConsolidator.Api
```

Swagger UI is public at `/swagger/`; the OpenAPI document is at `/openapi/v1.json`. `POST /api/v1/uploads` accepts exactly one `file` part containing a UTF-8 JSON array, strictly smaller than 500,000 bytes, and requires `Idempotency-Key` (UUID v4) plus `X-Api-Key`. The API key is supplied through `Security__ApiKey` configuration/environment only and is compared in constant time. A newly accepted upload returns `202 Accepted` with upload ID, current status, display filename, SHA-256, and a `Location` header pointing to the live `/api/v1/uploads/{uploadId}/status` endpoint. Missing or invalid credentials return the same `401` response. Invalid multipart/JSON or idempotency headers return `400`, content types other than multipart return `415`, oversized files return `413`, and a reused idempotency key with different bytes returns `409`. Every error has `traceId`, `code`, and `message`. Repeating a key with identical bytes returns the same upload. The background worker handles accepted uploads and writes their immutable report. Do not commit local environment files or secret-bearing settings.

## Public read API (Milestone 6)

All GET routes below are public and require no API-key header. In Swagger, expand a GET operation and select **Try it out**; only the POST needs credentials.

| Route | Queries and response |
| --- | --- |
| `GET /api/v1/uploads` | Optional `status`, `page=1`, `pageSize=25`. Returns upload attempts ordered by start instant descending, then ID descending. Status accepts named upload states, case-insensitively. |
| `GET /api/v1/uploads/{uploadId}/status` | `page=1`, `pageSize=50`. Returns `upload` metadata, `traceId`, safe failure code/message, and paginated `items` ordered by source index. |
| `GET /api/v1/uploads/{uploadId}/report` | Downloads the final immutable report as `application/json` with attachment filename `{uploadId}.json`. |
| `GET /api/v1/catalog` | Optional `category`, `brand`, `name`, `sellerName`, `page=1`, `pageSize=25`. Returns canonical products ordered by ID ascending, each with `sellerOffers`. |
| `GET /api/v1/health` | Returns `200 {"status":"healthy"}` without database/filesystem checks. |
| `GET /api/v1/ready` | Returns `200 {"status":"ready"}` when the existing SQLite database opens with foreign keys enabled and staging/report roots accept temporary flushed writes; otherwise `503 not_ready`. Probe files are deleted on close. |

Pagination responses contain `items`, `pageNumber`, `pageSize`, and `totalCount`. Pages must be positive integers; page size is 1–100 on all paginated routes. Invalid values return `400 invalid_pagination`; an unsupported upload-state filter returns `400 invalid_status`. Pages beyond the result set return an empty `items` array. Offset pagination is stable for unchanged data; new uploads or catalog changes can move subsequent pages.

Upload metadata includes `uploadId`, display `fileName`, `status`, start/completion timestamps, summary counts (`received`, `approved`, `cleaned`, `rejected`), and `reportAvailable` based on finalized durable metadata. Status items include source index/ID, raw seller/name/brand/category, cleaned values, outcome, action taken, and canonical product ID. Public projections exclude idempotency keys, staging/report paths, and internal diagnostics. Report-generation retries remain visible as `ReportPending`, without exposing their exception details.

Catalog category/brand/seller filters match whole values; product name matches a literal substring. All filters ignore case, accents, and leading/trailing/repeated whitespace; blank filters are omitted. Queries use bound SQLite parameters, so `%`, `_`, and SQL-looking text remain literal input. A product appears once regardless of seller count. With `sellerName`, only matching offers are returned; otherwise all offers are returned in offer-ID order. Offers expose only seller name and seller source-product ID.

Unknown or invalid upload IDs return `404 upload_not_found` with the standard envelope. Reports return `409 report_not_available` while queued, processing, or report-pending. Terminal reports require complete durable metadata, the expected generated path, matching SHA-256, valid JSON, and the expected upload identity/state. Missing, corrupt, or misdirected terminal reports return a safe `500 report_unavailable` and structured log evidence; this route never repairs or regenerates a report. It verifies and buffers the exact file bytes before sending them. Availability flags do not perform per-row filesystem checks; download verifies the file itself.

Example public queries:

```http
GET /api/v1/uploads?status=Completed&page=1&pageSize=25
GET /api/v1/catalog?category=Electronics&brand=Samsung&name=Galaxy&page=1&pageSize=25
GET /api/v1/health
GET /api/v1/ready
```

Configuration keys:

| Environment variable | Purpose |
| --- | --- |
| `Security__ApiKey` | Required upload credential; never place in source control, logs, reports, query strings, or OpenAPI examples |
| `Development__UsePlaceholderApiKey` | Explicit opt-in to the public local placeholder, accepted only in Development |
| `Catalog__StorageRoot` | Persistent data root, including the working `catalog.db`, staged uploads, and reports; consumed by startup and file adapters; defaults to `/home/data` on Linux and the user's local application data directory on Windows |
| `Catalog__StarterDatabasePath` | Optional path to the immutable starter database; consumed by bootstrap and defaults to `catalog.db` beside the running application |
| `ASPNETCORE_HTTP_PORTS` | HTTP listener port; container baseline uses `8080` |

Infrastructure consumers can use `FileSystemStoragePaths`, `SqliteWorkingDatabaseBootstrapper`, and `SqliteDatabaseMigrator` to initialize storage. At startup, `Processing` uploads return to `Queued` while their committed item outcomes remain persisted; the worker skips those indexes when resuming. Each valid item's product change, seller link, and `UploadItem` result share one transaction. When processing finishes, durable counts and intended outcome are stored as `ReportPending`; the report is atomically published, read back, and hashed before the upload receives its terminal state. Report failures remain retryable. For local upload testing, set `Security__ApiKey` in the shell environment. The deployed app must be HTTPS-only at its ingress.

## Verification

All changes follow **feature branch -> pull request -> user review -> user merge to main**. Do not push project code directly to `main`, merge on the user's behalf, or enable auto-merge. See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

Run the repository verification script from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

```sh
sh scripts/verify.sh
```

These scripts restore, build Release, run tests (including workflow-YAML parsing), and check formatting without modifying source. CI runs the verifier and a separate credential-free secret-scanning job on pushes and PRs. Gitleaks 8.30.1 uses default rules plus generated-output exclusions in `.gitleaks.toml`; source, tests, examples, and documentation remain scanned. The scanner download is checked against its release checksums. CI scans both checkout contents and complete Git history without a license/token secret or Python tooling.

Run the same checkout scan locally:

```powershell
gitleaks dir . --config .gitleaks.toml --redact --no-banner
# Once the checkout has Git history:
gitleaks git . --config .gitleaks.toml --redact --no-banner
```

## Operational hardening (Milestone 7)

Uploads use a fixed window of **5 requests per minute per connection peer**; the six public read routes and OpenAPI share a separate **120 requests per minute** policy. No requests queue. Rejections return `429 rate_limit_exceeded` with the standard error envelope and `Retry-After` when provided by the limiter. Invalid/unauthorized requests also consume permits, and rate rejection happens before parsing or staging. The peer comes from the server connection; arbitrary `X-Forwarded-For` headers do not influence partitions. Automatic forwarded-header trust must remain disabled. A future deployment must explicitly review trusted proxies; shared ingress peers may share a bucket. Limits are per process, intended for the single-instance lab.

Kestrel and the upload-body wrapper cap the total multipart request at **532,768 bytes**, allowing multipart overhead while independently enforcing the existing file limit of **499,999 bytes**. Declared and streamed sizes are checked. Global exceptions return a generic `500 internal_error` envelope with a trace ID. Application error logs include exception type and trace/upload IDs, not exception messages, headers, keys, raw source bodies, or physical paths. No request-body/header logging is enabled; framework informational request logs are disabled by the default logging configuration.

Responses include `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, and a restrictive camera/microphone/geolocation permissions policy. HTTPS responses also receive HSTS. No CSP is imposed on the CDN-backed Swagger page, preserving its current behavior. HTTPS enforcement remains the deployment ingress's responsibility.

## Docker

Build and run the non-root baseline image:

```sh
docker build -t marketplace-catalog-consolidator:local .
docker run --rm -p 8080:8080 -v marketplace-data:/home/data marketplace-catalog-consolidator:local
```

The runtime image baseline uses the .NET 10 ASP.NET base image, listens on port `8080`, and runs as UID/GID `10001`. Persistent storage is mounted at `/home/data`. Image build/run/inspection/verification is intentionally deferred to the final Azure deployment milestone and was not performed for Milestone 7. CI has no image jobs.

## Azure deployment

The target is Azure App Service Linux F1 in West Central US, running the published Docker image. Configure persistent storage for `/home/data`, set `Security__ApiKey` through App Service application settings, and require HTTPS at the App Service ingress. Keep the starter database immutable and ensure its working copy and all upload/report data remain on persistent storage. No cloud resources or deployments are created by this repository setup milestone.

## Known limitations

- Azure deployment remains future work. Process-local rate limits need review with the final ingress/proxy topology.
- Public pagination uses offsets; concurrent writes can shift page membership. Report downloads buffer verified bytes in memory, appropriate for this lab's bounded upload size.
- Azure App Service F1 storage persistence and capacity must be verified against the chosen hosting configuration before relying on it for production data.
