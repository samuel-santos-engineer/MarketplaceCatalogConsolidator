# Marketplace Catalog Consolidator — Milestone 11 Codex Prompt

## Goal

Add a small, polished, framework-free browser UI that consumes the existing API. It must use only new static HTML, CSS, and JavaScript files served by the existing ASP.NET Core static-files middleware.

The UI has two pages:

1. **Products** - query and paginate the public catalog.
2. **Uploads** - submit a JSON file, browse upload attempts, open an immutable report, and render a selected report’s summary and item table.

## Non-negotiable change boundary

**Do not modify, move, rename, format, regenerate, or delete any existing file.**

Only add new files beneath this exact directory:

```text
src/MarketplaceCatalogConsolidator.Api/wwwroot/
```

Before editing, inspect the directory. Do not overwrite an existing file, including the existing Swagger assets. If a required target filename already exists, stop and report the conflict rather than changing it.

At handoff, prove the boundary with `git diff --name-status main...HEAD`: every changed path must have status `A` and must be below the directory above. Do not change C#, project files, tests, API contracts, configuration, Docker files, documentation, dependencies, package files, or CI.

## Branch and delivery rules

- Start from current `main` with a clean working tree.
- Create branch `feat/static-catalog-ui`.
- Commit only the new static files, push the branch, and open one pull request.
- Do not merge, enable auto-merge, run Docker, publish an image, or do Azure work.
- Stop after opening the PR for human review.

## Required new files

Use clear names, for example:

```text
wwwroot/index.html
wwwroot/uploads.html
wwwroot/css/catalog-ui.css
wwwroot/js/catalog-ui.js
wwwroot/js/uploads-ui.js
```

You may add other **new** static files only when necessary. Do not use a framework, build system, package manager, CDN, external JavaScript/CSS, chart library, icon library, or server-side template.

## Shared UI requirements

- Use semantic HTML, UTF-8, responsive layout, keyboard-visible focus states, accessible labels, and live regions for loading/success/error messages.
- Use a restrained blue-pastel visual system: pale blue background, white/light-blue cards, blue navigation/primary actions, high-contrast dark text, and clearly distinct success/warning/error colors. Respect `prefers-reduced-motion`.
- Provide the same simple top navigation on both pages: **Products** (`/`) and **Uploads** (`/uploads.html`). Keep Swagger separate; do not alter it.
- Use only same-origin relative API URLs. Do not add CORS configuration.
- Use `fetch`, `URLSearchParams`, `AbortController`, and DOM construction with `textContent`; never inject API values with `innerHTML`.
- Show safe, human-readable HTTP/network failures. Never display or log API-key values, request headers, or raw file contents.

## Products page

Build the landing page at `/` using `GET /api/v1/catalog`.

- Provide filters for category, brand, product name, and seller name.
- Provide Search and Reset controls. Submitting/changing filters resets to page 1.
- Call the API with `category`, `brand`, `name`, `sellerName`, `page`, and `pageSize`; omit blank filters.
- Render a responsive table of the returned canonical products with columns appropriate to the actual response: ID, name, brand, category, and seller offers.
- Render seller offers safely as readable text (for example seller name and source product ID in a compact list), without duplicating product rows.
- Implement previous/next controls, current page, total count, disabled boundary states, loading state, empty state, and errors. Use the API’s returned `pageNumber`, `pageSize`, and `totalCount`; do not invent totals.
- Abort a stale request when a newer search is started.

## Uploads page

### Upload form

Add a clearly labelled upload form that calls the existing `POST /api/v1/uploads` endpoint.

- File input accepts `.json` only and explains the server-enforced strictly-less-than-500,000-byte limit.
- API-key input is `type="password"`, labelled as required for upload only, and is held only in the input/JavaScript memory for the current page. Do not use localStorage, sessionStorage, cookies, URLs, page markup, console logs, or examples containing a real secret.
- Generate a fresh UUID v4 for `Idempotency-Key` via `crypto.randomUUID()`; if unavailable, request existing `GET /api/v2/random-uuid`. Display the generated key only as a normal UI value that the user may copy/regenerate before submitting; do not persist it.
- Submit one multipart `file` part with headers `X-Api-Key` and `Idempotency-Key`.
- Client-side checks improve usability but do not replace server validation: require a selected `.json` file and reject files at or above 500,000 bytes before sending.
- On `202 Accepted`, show the returned upload ID/status and refresh the upload list. Respect a `Location` header if returned. On a duplicate idempotent response, present its returned upload information without treating it as a client failure.
- Do not attempt to read or parse the selected JSON file in the browser.

### Upload attempt list

Consume public `GET /api/v1/uploads`.

- Provide a status filter plus pagination with the same robust behavior as the products page.
- Render each upload’s ID, filename, status, start/completion times, received/approved/cleaned/rejected counts, and report availability.
- For every upload, provide a report link with exact path `/api/v1/uploads/{uploadId}/report`. It may open in a new tab. When a report is not yet available, communicate that clearly and still provide a status link to `/api/v1/uploads/{uploadId}/status`.
- Provide a **View in page** control for finalized reports. Its URL state should be `uploads.html?uploadId=<UUID>` so it is shareable and browser navigation works.

### Selected report view

When `uploadId` is present, request `/api/v1/uploads/{uploadId}/report`.

- If the report is available, render a clear report heading (file name/upload ID/status/timestamps), then a small summary table with received, approved, cleaned, and rejected counts.
- Render a pie chart without any library: use accessible HTML/CSS (for example a `conic-gradient`) plus a labelled legend. The chart and legend must derive directly from the report summary counts. Include a textual summary so the data remains available without color perception or CSS.
- Below the summary, render a simple responsive item table using the report items. Include source ID, seller, name, brand, category, status, and action taken. Values must be inserted with `textContent`.
- If the report is pending (`409`) or unavailable, show an explanatory state and link back to the upload status endpoint; do not fabricate report data.
- Validate `uploadId` as a UUID before requesting it. Invalid URL input must not be interpolated into the DOM or endpoint path.

## Scope and quality checks

- Re-read the current live OpenAPI or existing endpoint response contracts before coding; use their actual JSON property names.
- Do not create endpoints or change any backend behavior.
- Manually run the API locally and verify Products, Uploads, upload validation, a successful upload with the configured Development placeholder, pagination, report link/download, in-page report table, and pie-chart count reconciliation.
- Verify at narrow and wide viewport widths, keyboard navigation, and browser refresh/back navigation on `uploads.html?uploadId=...`.
- Run the existing repository verifier only if it does not modify tracked files. Report its result.
- In the PR handoff, include the PR URL, branch, commit SHA, new-file inventory, manual browser evidence, verifier result, and the `git diff --name-status main...HEAD` proof that every change is an allowed addition.
