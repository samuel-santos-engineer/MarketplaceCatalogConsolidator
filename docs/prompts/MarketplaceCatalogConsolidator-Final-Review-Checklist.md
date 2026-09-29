# Marketplace Catalog Consolidator — Final Review Checklist

**Score: `0 / 100`**

Mark an item only when its evidence is present in the merged source and automated tests. The total is the sum of checked items. **100/100** means the submission meets every review requirement below.

> **Acceptance rule:** a numerical score of 100 is valid only when every mandatory assessment gate at the end of this checklist is also checked.

## 1. Robust matching and edge-case safety — 35 points

- [ ] **8** Matching is strict normalized `Brand + Name + Category`; a missing canonical value never produces an unsafe match.
- [ ] **7** Normalization handles trim, repeated whitespace, casing, and diacritics/accents before comparison. It preserves meaningful punctuation/model information rather than removing characters indiscriminately.
- [ ] **5** `Photo` is canonicalized to `Photography`; valid unfamiliar brand/category values are preserved after cleaning rather than discarded. Missing values follow the separately documented incomplete-identity policy.
- [ ] **7** A legitimate same product from another seller links to the existing master product rather than creating another `Product` row.
- [ ] **4** The input contract is assessed for stable identifiers. The current source has no EAN/UPC/ISBN/MPN; if such an identifier is later added, its deterministic matching precedence is documented and tested.
- [ ] **4** No fuzzy string-distance algorithm is used for automatic merges. Near matches such as `Galaxy S22`/`Galaxy S23` and `iPhone 13 128GB`/`256GB` remain distinct. README explains why this strict policy avoids false positives; fuzzy matching is a future, human-review-only option.

## 2. Database architecture, integrity, and performance — 30 points

- [ ] **7** `Product` remains the canonical master catalog and seller offers are represented through `SellerProduct` links.
- [ ] **5** `SellerProductId` uses `TEXT`, and idempotency/uniqueness constraints prevent duplicate seller-offer writes.
- [ ] **6** Each imported item’s product lookup/create, seller association, and item outcome are committed in one SQLite transaction.
- [ ] **3** All SQL uses parameters; no source value is interpolated into a SQL statement.
- [ ] **2** Serialized worker/maintenance locking prevents concurrent uploads or reset operations from corrupting SQLite or filesystem state.
- [ ] **2** README documents why the design uses **independent per-item transactions** rather than one all-or-nothing file transaction: durable progress and restart recovery outweigh rollback of a bounded lab upload. It explicitly identifies full-batch atomicity as an alternative policy, not an omitted safeguard.
- [ ] **3** Matching queries use indexed canonical identity values and do not execute an avoidable full catalog scan per input item. Document the observed approach and its bounded-data trade-off.
- [ ] **2** Document the growth path from SQLite to PostgreSQL and catalog-search/indexing options; do not claim that vector search is needed for the current deterministic matching contract.

## 3. Comprehensive automated coverage — 25 points

- [ ] **8** Unit/integration tests prove matching across case, accent, and repeated-spacing variations.
- [ ] **6** Tests prove distinct seller IDs can link to one canonical product without creating duplicate master products.
- [ ] **4** Tests prove close-but-distinct names and capacities/generations (including `Galaxy S22` versus `Galaxy S23`) do not consolidate.
- [ ] **3** Tests prove duplicate source IDs and conflicting duplicate source IDs are rejected with the documented outcome.
- [ ] **2** Tests prove transactional behavior: a failed item cannot leave a product or seller link partially written.
- [ ] **2** Tests prove matching and linking behavior with the supplied JSON input, including no new master row for a legitimate duplicate seller offer.

## 4. Documentation and reviewer usability — 10 points

- [ ] **3** A reviewer can start the project with one documented command (`dotnet run` for local use); prerequisites and Development placeholder behavior are clear.
- [ ] **2** README explains the strict matching strategy, normalization rules, false-positive/false-negative trade-off, and the audit trail (`Action Taken` in status/report output).
- [ ] **2** README and Swagger provide a short happy-path walkthrough: upload, status, report, and catalog query.
- [ ] **2** Architecture documentation explains SQLite’s current fit and credible scale-out options: PostgreSQL plus database/index/search changes as data and query needs grow.
- [ ] **1** Full verification passes: Release build, all unit/integration tests, formatting, and secret scans.

## Evidence to attach before final review

- [ ] Link to the pull request and successful CI run.
- [ ] Current `README` test total and local verification output.
- [ ] Screenshot or short Swagger walkthrough: upload, status, report, and catalog query.
- [ ] One report example showing Approved, Cleaned, and Rejected outcomes.

## Mandatory assessment-alignment gates (not additional points)

- [ ] The repository is public and its submitted URL is ready to reply to the assessment email.
- [ ] The separate Guideline Document referenced by the assessment has been reviewed, and its required structure, naming, and documentation conventions are demonstrably followed. **Do not check this until that document is available.**
- [ ] An end-to-end test or reproducible local walkthrough imports the supplied `ProductEntry.json` (269 entries) through the real upload workflow, waits for completion, and verifies that report counts reconcile to the input count.
- [ ] The same end-to-end evidence verifies the required marketplace behavior: duplicate canonical products do not create new `Product` rows, while the respective sellers receive `SellerProduct` links to the existing product.
- [ ] The post-import evidence verifies no duplicate canonical product signatures and that the immutable source `artifacts/catalog.db` was not modified.
- [ ] The README explicitly records the intentional ambiguity decisions: normalization scope, strict-match identity, preservation of valid unfamiliar brand/category values, treatment of incomplete fields, seller/source-ID duplication policy, and why the selected choices favor a false negative over an unsafe false-positive merge.

## Final decision

- [ ] **100/100 — Ready for final submission**

Reviewer notes:

>
