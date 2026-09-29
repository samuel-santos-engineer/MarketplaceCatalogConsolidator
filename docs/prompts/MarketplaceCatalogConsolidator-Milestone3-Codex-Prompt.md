# Milestone 3 - Source Parsing and Per-Item Catalog Consolidation

Milestone 2 is accepted. Read these documents before changing code:

1. `MarketplaceCatalogConsolidator-Codex-Instructions.md`
2. `MarketplaceCatalogConsolidator-Solution-Design.md`
3. `MarketplaceCatalogConsolidator-Milestone2-Codex-Prompt.md`

Implement **Milestone 3 only**. Keep JSON report generation and HTTP upload handling for their separately scoped milestones.

## Goal

Implement source JSON parsing and the production per-item consolidation processor used by the serialized workflow. It must clean/validate input, strictly match canonical products, write seller offers, persist item outcomes, and remain restart-safe through the workflow contracts established in Milestone 2.

## Source contract

The source is a UTF-8 JSON array whose items have this shape:

```json
{
  "Id": "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d",
  "SellerName": "MegaStore",
  "Name": "Smartphone Galaxy S23",
  "Brand": "Samsung",
  "Category": "Electronics"
}
```

Parse safely with `System.Text.Json`. Preserve source index and raw values so outcomes remain stable across restart/retry. Malformed documents or invalid source entries must become clear domain/application outcomes, not unhandled worker crashes.

## Required consolidation behavior

For every source item, in the individual transaction boundary established by Milestone 2:

1. Validate a non-empty seller name and product name, plus a GUID-shaped source `Id`.
2. Apply the agreed SQL-control-sequence business-validation policy to text fields. Parameterized SQL remains mandatory for every database command regardless of this policy.
3. Trim values and collapse internal whitespace.
4. Normalize case and remove diacritics for comparison.
5. Canonicalize category:
   - `Photo` becomes `Photography`.
   - Resolve known categories against distinct non-null `Product.Category` values using normalized comparison.
   - Unknown or empty category becomes `NULL`.
6. Canonicalize brand:
   - Resolve against distinct non-null `Product.Brand` values using normalized comparison.
   - Unknown or empty brand becomes `NULL`.
7. Strictly match an existing product only when cleaned brand, name, and category are all present. The matching key is normalized `Brand + Name + Category`.
8. On a match, create the `SellerProduct` link for the canonical product. Do not create another `Product` row.
9. Without a match, create a new `Product` with cleaned values and normalized identity fields, then create the seller link. Brand/category may be `NULL`; such incomplete identity must not match an existing product.
10. Persist an item result with raw fields, cleaned fields, status, human-readable action(s), and matched/created product ID.

## Outcome rules

| Situation | Required status | Required action shape |
|---|---|---|
| Unchanged new product | `Approved` | Created product and linked seller |
| Unchanged cross-seller duplicate | `Approved` | Linked seller to existing product |
| Changed new product | `Cleaned` | List each field before/after; created and linked |
| Changed cross-seller duplicate | `Cleaned` | List each field before/after; linked existing |
| Invalid/suspicious input | `Rejected` | Precise validation or security-policy reason |
| Repeated seller/source ID, same fingerprint | `Rejected` | Duplicate source entry |
| Repeated seller/source ID, different fingerprint | `Rejected` | Source-ID conflict |

`Approved` and `Cleaned` describe input quality, not whether the canonical product already existed.

The input `TestBrand'; SELECT 1; --` must be rejected with an action equivalent to `Brand contains a suspicious SQL control sequence`. Ordinary apostrophes or punctuation alone must not be rejected.

## Persistence requirements

- Use only parameterized SQLite commands.
- Use the migrated `SellerProduct.SellerProductId` text field for source IDs.
- Before inserting a seller offer, check `(SellerName, SellerProductId)` and compare the persisted source fingerprint.
- Enforce idempotency and conflict policy without relying only on application memory.
- Keep canonical product lookup/create and seller-offer write in the same item transaction.
- Do not mutate existing canonical `Product` display values while linking a seller offer.
- Do not introduce report file writes, upload-file staging logic, endpoint code, or a web UI.

## Required tests

Add focused unit and disposable-SQLite integration tests proving at least:

1. Source parsing preserves raw input values and source indexes.
2. Whitespace cleanup produces `Cleaned`; e.g. `Smartphone  Galaxy S23` matches `Smartphone Galaxy S23`.
3. Accent-insensitive comparison links `Câmera Canon EOS R6` to the existing `Camera Canon EOS R6` product.
4. `Photo` cleans to `Photography` and matches the canonical category.
5. A valid second seller for an existing product creates a seller link but no second product.
6. A new product is created when strict canonical matching fails.
7. Unknown brand/category become `NULL` and cannot cause an unsafe existing-product match.
8. Suspicious SQL-control-sequence input is rejected and cannot execute SQL.
9. Normal names containing ordinary apostrophes/punctuation are accepted.
10. Duplicate same-seller/source-ID input is rejected, while conflicting data under the same pair is explicitly rejected as a conflict.
11. Item outcomes include correct status, before/after actions, and product identity.

Tests must use temporary working databases and must not mutate the supplied starter database.

## Engineering constraints

- .NET 10 only. Do not create or use Python files, virtual environments, requirements files, or Python-based repository scripts.
- Preserve the clean dependency direction and use the existing ports/workflow contract.
- Keep parsing, normalization, policy, and persistence responsibilities separately testable.
- Use cancellation tokens and dispose SQLite resources correctly.
- Do not implement report generation, HTTP upload handling, endpoint mappings, Swagger changes, authentication, rate limiting, cloud resources, image publication, GitHub operations, or a web UI.

## Completion protocol

1. Inspect the current repository and identify how Milestone 2 invokes its item processor.
2. State a concise implementation plan before editing.
3. Implement only the requested milestone.
4. Run the repository verifier, release build, tests, and formatting checks.
5. Report changed files, exact commands run, test results, and any limitations.
6. Stop after the Milestone 3 handoff. Propose the next milestone but do not proceed automatically.
