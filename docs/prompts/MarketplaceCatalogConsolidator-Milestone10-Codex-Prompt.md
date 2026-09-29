# Marketplace Catalog Consolidator — Milestone 10 Codex Prompt

## Goal

Add one focused **test-only** integration test proving the supplied 269-entry fixture succeeds through the real HTTP boundary and preserves the catalog-consolidation invariants.

This replaces the previously planned Milestone 10 UI work. The static HTML UI becomes the future Milestone 11.

## Branch and scope rules

- Start from current `main` and confirm a clean working tree.
- Create branch `test/supplied-fixture-http-end-to-end`.
- Change only test source files under `tests/MarketplaceCatalogConsolidator.IntegrationTests/`. Prefer adding one new clearly named test file rather than editing production code.
- Do not change API/Application/Domain/Infrastructure production code, database migrations, artifact inputs, configuration, README/design documents, CI, Docker, Azure resources, or existing endpoint contracts.
- Do not run Docker, publish an image, or do Azure work.
- Commit, push, and open one PR. Do not merge, enable auto-merge, or commit directly to `main`.

## Test scenario

Implement an integration test using the actual ASP.NET Core test host and the actual `POST /api/v1/uploads` endpoint. It must not call the worker, parser, processor, or SQLite stores directly to bypass HTTP acceptance.

Use the committed `artifacts/ProductEntry.json` fixture exactly as the uploaded multipart `file`. It contains 269 entries and is below the server size limit.

Configure the test host with an isolated temporary `Catalog:StorageRoot`, the actual immutable `artifacts/catalog.db` as `Catalog:StarterDatabasePath`, and a test-only `Security:ApiKey`. Use the normal hosted worker; do not replace it with an idle/fake processor.

### Required flow

1. Before submission, calculate and retain a SHA-256 hash of the immutable starter database.
2. Capture the starter baseline from the working database after normal bootstrap/migration:
   - verify 975 initial `Product` rows and zero `SellerProduct` rows;
   - record counts for every **complete** normalized product identity (`NormalizedBrand`, `NormalizedName`, `NormalizedCategory` all non-null).
3. Send a real multipart POST with one JSON file, a fresh UUID v4 `Idempotency-Key`, and the test API key.
4. Assert `202 Accepted`, parse the returned upload ID, and poll the public status endpoint using a bounded deterministic timeout until the upload is terminal and `reportAvailable` is true. Do not use unbounded waits or arbitrary long sleeps.
5. Download the immutable report from `GET /api/v1/uploads/{uploadId}/report` and use its actual contract fields for assertions.

## Required assertions

### HTTP and report lifecycle

- The request is accepted through the real HTTP endpoint.
- The final report is valid JSON for that upload and has exactly 269 item outcomes.
- `received == 269` and `approved + cleaned + rejected == received` in both durable upload metadata and the immutable report.
- The downloaded report identity/status/counts agree with the final public status response.

### Corrected canonical-identity invariant

Do **not** assert that every final product identity is globally unique. The supplied starter catalog may already contain historical duplicate normalized identities, and this application deliberately does not destructively merge existing catalog rows.

Instead, for every complete normalized identity after import, assert:

```text
postImportCount <= max(baselineCount, 1)
```

This proves the import did not introduce a second canonical product for any complete identity while still allowing a previously absent identity to be created once. Document this baseline-aware meaning in the test name/comments only; do not change documentation files in this test-only PR.

### Expected seller-link set

The starter table has zero seller links. Derive the expected set from the final report’s accepted item outcomes (`Approved` and `Cleaned`) using the **actual cleaned/canonical seller name and source product ID fields defined by the report contract**.

Assert that the final `SellerProduct` set exactly equals that expected set:

- each expected `(SellerName, SellerProductId)` appears exactly once;
- its `ProductId` equals that item’s reported matched/canonical product ID;
- no extra seller link exists;
- rejected outcomes do not create an additional seller link. If a rejected duplicate shares a seller/source ID with an accepted earlier item, the assertion must correctly treat the accepted link as the single expected row.

### Immutable input

- Recalculate the starter database SHA-256 after processing and assert it matches the original hash.
- Assert all runtime data remains under the isolated temporary working root, not the repository artifact directory.

## Test quality

- Reuse existing fixture/test-host conventions and helper types where practical, but keep isolation, disposal, and timeout handling robust.
- Query SQLite only for baseline/post-import verification. Never manipulate rows to make the scenario pass.
- Do not hard-code outcome totals other than the known input count of 269; derive approved/cleaned/rejected expectations from the returned report.
- The test must be deterministic and safe to run in parallel with the existing suite.
- Keep the existing full test suite unchanged except for the added coverage.

## Verification and handoff

Run:

```powershell
dotnet format MarketplaceCatalogConsolidator.sln --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
gitleaks dir . --config .gitleaks.toml --redact --no-banner
gitleaks git . --config .gitleaks.toml --redact --no-banner
```

Also run the focused new test once and report its exact result. In the handoff provide the PR URL, branch, commit SHA, changed test files, new total test count, verification results, and confirmation that no production code, Docker, or Azure operation changed. Stop for review; do not merge.
