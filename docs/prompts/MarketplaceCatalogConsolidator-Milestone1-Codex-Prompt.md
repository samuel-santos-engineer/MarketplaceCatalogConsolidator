# Milestone 1 - SQLite Bootstrap and Migration

Milestone 0 is accepted. Read these repository documents before changing code:

1. `MarketplaceCatalogConsolidator-Codex-Instructions.md`
2. `MarketplaceCatalogConsolidator-Solution-Design.md`

Implement **Milestone 1 only**. Do not add HTTP upload handling, endpoint mappings, Swagger changes, Azure resources, image publication, GitHub operations, or a web UI.

## Goal

Deliver a safe SQLite working-copy bootstrap, forward-only schema migration, core domain/application ports, and migration-focused test proof.

## Mandatory scope

1. Treat the supplied starter `catalog.db` as immutable. The running database must be a separate working copy in the configured data directory.
2. Create the data-path abstraction/configuration needed for local development and the deployed `/home/data` location. Do not create production data inside the repository.
3. Enable SQLite foreign-key enforcement on every connection.
4. Implement an idempotent, forward-only migration mechanism. It must record applied migrations and safely migrate a database created from the 975-product starter catalog.
5. Preserve the `Product` and `SellerProduct` concepts. Migrate `SellerProduct.SellerProductId` from integer storage to `TEXT NOT NULL`, retaining all existing data if future source databases contain seller links.
6. Add the persistence schema required by the approved design:
   - normalized product identity fields and lookup index;
   - seller-source idempotency support using `(SellerName, SellerProductId)`;
   - `Upload` and `UploadItem` operational/audit tables;
   - migration metadata.
7. Define domain types and application-facing ports for:
   - working database bootstrap;
   - migrations;
   - product catalog lookup/write;
   - seller offer persistence;
   - upload persistence and upload-item progress;
   - atomic report/staging file interfaces.

Ports must not depend on SQLite or ASP.NET Core types. SQLite and file-system implementations belong in Infrastructure.

## SQL-control-sequence policy

This is a demonstrative business-validation rule. It is not the primary SQL-injection defense; **every database operation must use parameterized SQL regardless of validation outcome**.

Reject a text field only when it contains:

- a semicolon followed by a SQL keyword: `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `PRAGMA`, `ATTACH`, `DETACH`, `UNION`, `EXEC`, or `EXECUTE`; or
- a SQL comment marker (`--`, `/*`, `*/`) combined with one of those SQL keywords.

Do not reject ordinary apostrophes or product punctuation alone.

The following must be rejected with an action equivalent to `Brand contains a suspicious SQL control sequence`:

```text
TestBrand'; SELECT 1; --
```

The actual input-validation endpoint is out of scope for this milestone. Put the reusable policy in the domain/application layer with unit tests.

## Required tests

Add or extend tests proving at least:

- bootstrap from the starter asset creates a separate working database;
- migration preserves all 975 existing `Product` rows;
- foreign keys are enabled and enforced on a normal application connection;
- migrated `SellerProduct.SellerProductId` stores a GUID-shaped string;
- migration is idempotent;
- expected tables, columns, indexes, and unique constraints exist after migration;
- the SQL-control-sequence policy rejects the supplied suspicious value while accepting normal apostrophes/punctuation.

Tests must use disposable temporary directories/databases and must not mutate the supplied starter asset.

## Engineering constraints

- .NET 10 only. Do not create or use Python files, virtual environments, requirements files, or Python-based repository scripts.
- Use `Microsoft.Data.Sqlite` or the repository's existing approved .NET SQLite dependency.
- Keep nullable analysis clean, dispose connections/commands/transactions correctly, and accept cancellation tokens where relevant.
- Keep the Docker baseline functional, but do not expand deployment work in this milestone.
- Update architecture/implementation documentation and README only where this milestone makes an existing statement incomplete or inaccurate.

## Completion protocol

1. Before editing, inspect the current repository state and briefly state the implementation plan.
2. Implement the milestone in small coherent changes.
3. Run the repository verifier, release build, tests, and formatting checks.
4. Report changed files, exact commands run, test results, and any limitations.
5. Stop after the Milestone 1 handoff and propose the next milestone. Do not proceed automatically.
