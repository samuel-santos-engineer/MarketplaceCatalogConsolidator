# Codex task — Refine the report table filters and prove the browser sees them

Work in Samuel's local `MarketplaceCatalogConsolidator` checkout in VS Code. This supersedes the previous report-item filter prompt. The supplied screenshots show the exact **Selected report** context: metadata, pie chart, **Outcome totals**, then the **Item outcomes** table. The requested interaction belongs inside this selected report, not in the separate **Upload attempts** list above it.

Samuel says the previous attempt did not produce the expected interface. First inspect the actual working tree and current browser response. Preserve any existing work; do not blindly add a second toolbar, duplicate listeners, or overwrite uncommitted changes. Diagnose whether the prior implementation is absent, placed in the wrong section, malfunctioning, or hidden by stale HTML/JavaScript. Briefly report that finding before editing.

## Scope and PR gate

- Change only the report UI and the minimum cache handling necessary to make that UI visible. Expected files: `src/MarketplaceCatalogConsolidator.Api/wwwroot/uploads.html`, `src/MarketplaceCatalogConsolidator.Api/wwwroot/js/uploads-ui.js`, and, only if essential to the header dropdown layout, `src/MarketplaceCatalogConsolidator.Api/wwwroot/css/catalog-ui.css`.
- Do not alter APIs, SQLite, report generation, upload behavior, the upload-attempt list, other pages, Docker, Azure, or unrelated artifacts. If correct HTML response caching requires editing server code outside these files, stop and ask Samuel before making that edit.
- Do not create a PR yet. Do not push or merge. Complete implementation and independently verify the actual interface in a browser first. At handoff, show Samuel the working behavior and changed-file list; wait for his decision on a PR.

## Item outcomes table interaction

1. Make the **Seller**, **Brand**, **Category**, and **Status** column captions interactive buttons inside their header cells. Clicking or keyboard-activating a caption opens a dropdown anchored to that column. The dropdown offers **All** plus the distinct values occurring in that column in the **entire selected report**, including a clear `(blank)` value if one exists. A choice filters report rows by exact displayed column value; do not implement fuzzy matching or substring search.
2. Filters compose with **AND** across columns. A user can select, for example, Seller `MegaStore`, Brand `Samsung`, Category `Electronics`, and Status `Approved`, and see only rows satisfying all four. Reopening a dropdown lets the user choose a different value or All for that column. Indicate active filters visibly, with an accessible name/state. Include an obvious **Clear filters** action.
3. Derive Seller/Brand/Category choices using the same raw-versus-cleaned value precedence used to render each table cell. Do not accidentally filter on a raw value while showing a cleaned value. Escape all untrusted report text through DOM text APIs; no `innerHTML` with report data.
4. Filter the in-memory immutable `report.items` first, then paginate the filtered rows. Keep a sensible default of 25 rows per page and the existing page-size options if already present. Changing any filter or page size resets to page 1. Show the matching count and `Page X of Y`, disable Previous/Next at boundaries, and show a clear empty state for no matches. No stale rows should survive a filter or report change.
5. Keep the table's existing columns, data, source order, styles, and responsive behavior. The filter dropdowns must work with mouse, keyboard, focus, and narrow layouts. Do not make a bare `<th>` clickable without a real button/control.

## Outcome totals shortcuts

- Turn the **Received**, **Approved**, **Cleaned**, and **Rejected** entries in the **Outcome totals** table shown in the screenshots into accessible button-like links to the item table. Keep the original numeric totals visible.
- Clicking `Rejected` clears other column filters, sets Status to `Rejected`, resets pagination, moves focus/scroll to the Item outcomes table, and shows the rejected rows. `Approved` and `Cleaned` behave equivalently for their status. Clicking `Received` clears all four filters and shows all received items. The shortcut should update the same filter state/dropdown UI as a manual header choice.
- The pie chart, legend, and Outcome totals remain totals for the complete immutable report, regardless of table filtering. For the supplied 269-item report, the totals remain Received 269, Approved 203, Cleaned 62, Rejected 4.

## HTML and JavaScript cache verification

- Inspect actual response headers and browser Network entries for `/uploads.html` and `/js/uploads-ui.js` before guessing that caching caused the previous failure. Confirm the page is served by the .NET app under test, not an unrelated static server or old process.
- Put an explicit release/version query token on the script URL in `uploads.html` (and the CSS URL only if CSS changes). The token must change with this revision, be stable within the release, and not be a random value on every request. Preserve the `uploadId` query parameter and direct report URL behavior.
- Verify that the HTML response itself is fresh on reload. A script query token in stale HTML cannot load new code; HTML `<meta http-equiv="Cache-Control">` is not a substitute for HTTP cache headers. If existing server behavior does not provide suitable HTML freshness, report the evidence and ask permission for the smallest server-side change; do not silently edit other files.
- Use a normal reload and a direct report URL refresh, then inspect the loaded script URL/version in Network. Do not claim the cache issue is fixed solely because a hard reload or disabled cache temporarily reveals the feature.

## Browser acceptance checks before any PR

Use a completed report at `/uploads.html?uploadId=<uuid>` and demonstrate visibly that the four header dropdowns appear **within Item outcomes** (the first screenshot's table). Test each individual column, combined filters, All/Clear filters, zero matches, page boundaries, page-size change from a later page, direct URL refresh, and narrow viewport. Test Received/Approved/Cleaned/Rejected shortcuts from **Outcome totals** (both screenshots' summary table). On the supplied report, Rejected shows exactly 4 rows and Approved 203 across pages; the pie chart and totals remain 269/203/62/4. Verify the **Upload attempts** status filter still affects upload attempts only.

Run the repository verifier, formatting check, JavaScript syntax check if Node is available, and `git diff --check`. Review `git diff --name-status` to confirm only the approved UI files changed. Provide screenshots or a concise browser interaction record plus the Network evidence for HTML and versioned JS. If browser functionality or caching is still uncertain, keep working locally and explicitly report the blocker; **do not create a PR** until Samuel has seen the expected working behavior.
