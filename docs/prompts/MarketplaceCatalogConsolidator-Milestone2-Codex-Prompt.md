# Milestone 2 - Startup Orchestration and Serialized Consolidation Workflow

Milestone 1 is accepted. Read these documents before changing code:

1. `MarketplaceCatalogConsolidator-Codex-Instructions.md`
2. `MarketplaceCatalogConsolidator-Solution-Design.md`
3. `MarketplaceCatalogConsolidator-Milestone1-Codex-Prompt.md`

Implement **Milestone 2 only**. Do not add HTTP upload handling, endpoint mappings, Swagger changes, Azure resources, image publication, GitHub operations, or a web UI.

## Goal

Implement startup orchestration and a single serialized consolidation workflow over the existing application ports. Prove independent item transactions, persisted progress, terminal-state calculation, and restart recovery with focused SQLite tests.

## Required behavior

### Startup recovery

- At application startup, recover persisted work before accepting new work through future adapters.
- `Queued` uploads remain eligible for processing.
- Uploads left in `Processing` by an interrupted process are returned safely to `Queued`.
- Preserve committed item outcomes. Recovery must not discard accepted, cleaned, or rejected item results already persisted.
- Recovery is idempotent: running startup recovery twice produces the same durable state as running it once.

### Serialized worker

- Add exactly one hosted/background worker for consolidation. It must use the established application abstractions, not expose HTTP behavior.
- The worker transactionally claims one `Queued` upload and transitions it to `Processing`.
- No two worker loops, service instances, or manual orchestration calls may claim the same upload concurrently.
- Do not hold a SQLite transaction while executing an item processor. Claim and state transitions are short transactions; each item is a separate short transaction.
- Avoid busy-waiting. Use a bounded wait/polling strategy and cancellation-aware shutdown.
- A worker restart must be safe even if it occurs between any two persisted steps.

### Item processing and progress

- Process each uncompleted source item independently.
- Persist a terminal item outcome immediately after its individual transaction succeeds.
- A rejected/failed item must not undo prior completed items or prevent later items from being evaluated.
- Use existing `UploadItem` uniqueness/progress records so recovery skips already completed source indexes.
- Do not implement the complete JSON parsing, normalization, cleaning, or product matching rules yet. Introduce only the application-level item-processing contract and deterministic test implementation needed to prove orchestration.
- Preserve the final domain semantics: later milestones will distinguish `Approved`, `Cleaned`, and `Rejected`; do not hard-code HTTP or presentation concerns into the worker.

### Upload outcome

- When all items have reached a terminal outcome, calculate and persist upload summary counts.
- Complete uploads with no rejected items as `Completed`.
- Complete uploads containing one or more rejected items as `CompletedWithRejections`.
- If an unrecoverable workflow/infrastructure error prevents completion, persist `Failed` with a non-sensitive failure code/message and do not mark it completed.
- Leave report-file generation out of this milestone unless an existing port requires a no-op/test implementation. The next milestone will implement external report generation.

## Required tests

Add focused tests using disposable SQLite databases and deterministic synchronization. Do not use arbitrary sleeps as correctness assertions.

At minimum, prove:

1. Startup recovery changes interrupted `Processing` uploads to `Queued` and preserves prior item outcomes.
2. A queued upload is claimed once and reaches the appropriate terminal upload state.
3. The worker processes at most one upload at a time; a second upload waits until the first has completed.
4. Each source item uses an independent transaction: an item rejected or failed by the test processor does not roll back already persisted item outcomes and does not prevent a later item from completing.
5. Restart/recovery skips item outcomes already persisted before interruption.
6. Summary counts and `Completed` versus `CompletedWithRejections` are correct.
7. Cancellation/shutdown does not corrupt SQLite state or leave a completed upload marked as processing.

Tests must not mutate the supplied starter database or use a production data path.

## Engineering constraints

- .NET 10 only. Do not create or use Python files, virtual environments, requirements files, or Python-based repository scripts.
- Use cancellation tokens consistently. Dispose async resources and SQLite transactions correctly.
- Preserve the dependency direction established in Milestone 0 and the ports established in Milestone 1.
- Keep worker implementation testable without running the HTTP host when possible.
- Do not broaden scope to source-file upload, source-file parsing, report writing, API-key enforcement, rate limiting, endpoint mapping, or deployment.
- Update architecture/implementation documentation only where this milestone changes an existing statement or needs a precise lifecycle clarification.

## Completion protocol

1. Inspect the current repository and identify the exact ports and migrations from Milestone 1 that will be used.
2. State a concise implementation plan before editing.
3. Implement only the requested milestone.
4. Run the repository verifier, release build, tests, and formatting checks.
5. Report changed files, exact commands run, test results, and limitations.
6. Stop after the Milestone 2 handoff. Propose the next milestone but do not proceed automatically.
