# Milestone 4 - Immutable Report Generation and Terminal Sequencing

Milestone 3 is accepted. Read these documents before changing code:

1. `MarketplaceCatalogConsolidator-Codex-Instructions.md`
2. `MarketplaceCatalogConsolidator-Solution-Design.md`
3. `MarketplaceCatalogConsolidator-Milestone3-Codex-Prompt.md`

Implement **Milestone 4 only**. Do not add HTTP upload handling. Do not build, run, inspect, publish, or verify a Docker image; image work is reserved for the final Azure deployment milestone.

## Goal

Generate exactly one immutable JSON report for every terminal upload outcome and sequence durable state so `Completed`, `CompletedWithRejections`, and `Failed` are persisted only after their report exists and is verified.

## Report contract

Create reports under the configured persistent report directory:

```text
/home/data/reports/{uploadId}.json
```

For local tests and development, use the configured local data root; never write reports into the repository.

The JSON report must contain:

- `uploadId`, `fileName`, `fileHash`, and `traceId`;
- upload start timestamp, consolidation-finished timestamp, report-generated timestamp, and terminal timestamp;
- final upload status and summary counts: received, approved, cleaned, rejected;
- failure code/message when terminal status is `Failed`, without secrets or stack traces;
- every persisted item outcome in stable source-index order, including source ID, seller name, name, brand, category, status, action taken, cleaned values when applicable, and canonical product ID when available.

Serialize with `System.Text.Json` and an explicit stable contract. Use UTC ISO 8601 timestamps. Do not depend on locale-sensitive formatting.

## Immutable file-writing requirements

1. Build the report only from durable `Upload` and `UploadItem` data, not transient worker memory.
2. Write to a unique temporary file in the same report directory.
3. Flush the stream to disk before publication.
4. Atomically move the completed temporary file to `{uploadId}.json` without replacing an existing final report.
5. Calculate and persist the final report SHA-256 and path in the `Upload` record.
6. A final report must never be modified or overwritten. A repeat attempt must verify and reuse the existing matching report, not create another final report.
7. Clean up stale temporary report files safely during startup/recovery without deleting final reports.

## Terminal-state sequencing

Introduce or use a non-terminal report-finalization state such as `ReportPending` if required. Update documentation and the persisted state model consistently.

Required sequence:

1. The worker finishes item processing and persists summary counts plus the intended terminal outcome.
2. The upload enters a non-terminal report-finalization state.
3. Generate, atomically publish, and verify the immutable report file.
4. In a short SQLite transaction, persist report path/hash, final timestamps, and the intended terminal status.

No upload may be persisted as `Completed`, `CompletedWithRejections`, or `Failed` before its final report exists.

If the process stops after report publication but before SQLite finalization, startup recovery must verify the existing report, attach its path/hash, and finish the database transition without replacing the report.

If report generation fails before publication, preserve enough durable diagnostics, keep the upload retryable in the non-terminal finalization state, and retry safely on recovery. Do not falsely mark it completed. If an unrecoverable workflow error produces a `Failed` terminal outcome, generate its failure report before persisting `Failed`.

## Required tests

Use disposable temporary directories and SQLite databases. At minimum, prove:

1. A successful upload produces one valid JSON report with correct metadata, summary, and stable item ordering.
2. `CompletedWithRejections` reports include approved/cleaned/rejected item actions and correct totals.
3. An unrecoverable failed upload has an immutable failure report before `Failed` is persisted.
4. Terminal database status is not persisted when injected report generation fails before publication.
5. Report publication followed by simulated process interruption is recovered without replacing or duplicating the final report.
6. Repeating report finalization for the same upload reuses a matching final report and preserves its bytes/hash.
7. Temporary files do not appear as downloadable final reports and are safely cleaned during recovery.
8. Report paths remain under the configured report root; uploaded file names cannot affect destination paths.

Avoid timing-based assertions. Use injectable file-system/report-writer abstractions or deterministic fault injection where needed.

## Scope boundaries

- Do not implement HTTP upload handling, endpoint mappings, Swagger changes, authentication, rate limiting, cloud resources, image publication, GitHub operations, or a web UI.
- Do not build or run Docker images in this milestone. If the repository verifier includes Docker steps, skip only those Docker-specific steps and report their intentional deferral.
- Do not change product matching or seller-offer policy except where a persisted report contract needs an existing field exposed.
- Keep all file-system work behind application-facing abstractions; do not couple it to ASP.NET Core.

## Completion protocol

1. Inspect the existing workflow state model, upload persistence, and recovery path before editing.
2. State a concise implementation plan before changing files.
3. Implement only Milestone 4.
4. Run the repository verifier, release build, tests, and formatting checks, omitting Docker-only verification.
5. Report changed files, exact commands run, test results, report/recovery evidence, and limitations.
6. Stop at the Milestone 4 handoff and propose the next milestone without proceeding automatically.
