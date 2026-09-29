"use strict";

const uploadForm = document.querySelector("#upload-form");
const uploadFile = document.querySelector("#upload-file");
const apiKey = document.querySelector("#api-key");
const idempotencyKey = document.querySelector("#idempotency-key");
const uploadResult = document.querySelector("#upload-result");
const listForm = document.querySelector("#uploads-filter");
const listRows = document.querySelector("#uploads-rows");
const listStatus = document.querySelector("#uploads-status");
const listEmpty = document.querySelector("#uploads-empty");
const listTable = document.querySelector("#uploads-table-wrap");
const listPageLabel = document.querySelector("#uploads-page");
const listPrevious = document.querySelector("#uploads-previous");
const listNext = document.querySelector("#uploads-next");
const reportPanel = document.querySelector("#report-panel");
const reportStatus = document.querySelector("#report-status");
const reportContent = document.querySelector("#report-content");
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
let listPage = 1;
let listTotalPages = 1;
let listRequest;
let reportRequest;

function element(name, text, className) {
  const result = document.createElement(name);
  if (text !== undefined) result.textContent = text;
  if (className) result.className = className;
  return result;
}

function showNotice(target, message, kind = "info") {
  target.replaceChildren(element("span", message));
  target.dataset.kind = kind;
  target.hidden = false;
}

async function readError(response) {
  try {
    const body = await response.json();
    return body.message || `Request failed with status ${response.status}.`;
  } catch {
    return `Request failed with status ${response.status}.`;
  }
}

async function generateUuid() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  const response = await fetch("/api/v2/random-uuid");
  if (!response.ok) throw new Error(await readError(response));
  return (await response.json()).uuid;
}

async function refreshUuid() {
  try {
    idempotencyKey.value = await generateUuid();
  } catch (error) {
    showNotice(uploadResult, error.message || "A new idempotency key could not be generated.", "error");
  }
}

function formatDate(value) {
  if (!value) return "Not completed";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString();
}

function appendCell(row, child) {
  const cell = document.createElement("td");
  if (typeof child === "string") cell.textContent = child;
  else cell.append(child);
  row.append(cell);
}

function statusPill(value) {
  const pill = element("span", value, "status-pill");
  pill.dataset.status = value;
  return pill;
}

function renderUploads(items) {
  listRows.replaceChildren();
  for (const upload of items) {
    const row = document.createElement("tr");
    const identity = document.createElement("div");
    identity.append(element("strong", upload.fileName), element("div", upload.uploadId, "mono muted"));
    appendCell(row, identity);
    appendCell(row, statusPill(upload.status));
    const timing = document.createElement("div");
    timing.append(element("div", `Started: ${formatDate(upload.startedAtUtc)}`), element("div", `Completed: ${formatDate(upload.completedAtUtc)}`, "muted"));
    appendCell(row, timing);
    const summary = upload.summary || {};
    appendCell(row, `Received ${summary.received ?? 0} · Approved ${summary.approved ?? 0} · Cleaned ${summary.cleaned ?? 0} · Rejected ${summary.rejected ?? 0}`);
    const links = element("ul", undefined, "link-list");
    const statusItem = document.createElement("li");
    const statusLink = element("a", "Status JSON");
    statusLink.href = `/api/v1/uploads/${encodeURIComponent(upload.uploadId)}/status`;
    statusItem.append(statusLink);
    links.append(statusItem);
    const reportItem = document.createElement("li");
    const reportLink = element("a", upload.reportAvailable ? "Download report" : "Report pending");
    reportLink.href = `/api/v1/uploads/${encodeURIComponent(upload.uploadId)}/report`;
    reportLink.target = "_blank";
    reportLink.rel = "noopener";
    if (!upload.reportAvailable) reportLink.setAttribute("aria-label", "Report pending; open the report endpoint");
    reportItem.append(reportLink);
    links.append(reportItem);
    if (upload.reportAvailable) {
      const viewItem = document.createElement("li");
      const view = element("a", "View in page");
      view.href = `/uploads.html?uploadId=${encodeURIComponent(upload.uploadId)}`;
      viewItem.append(view);
      links.append(viewItem);
    }
    appendCell(row, links);
    listRows.append(row);
  }
}

function listParameters() {
  const parameters = new URLSearchParams();
  const filter = listForm.elements.status.value;
  if (filter) parameters.set("status", filter);
  parameters.set("page", String(listPage));
  parameters.set("pageSize", listForm.elements.pageSize.value);
  return parameters;
}

async function loadUploads() {
  listRequest?.abort();
  listRequest = new AbortController();
  listStatus.textContent = "Loading uploads…";
  listTable.setAttribute("aria-busy", "true");
  listPrevious.disabled = true;
  listNext.disabled = true;
  try {
    const response = await fetch(`/api/v1/uploads?${listParameters()}`, { signal: listRequest.signal });
    if (!response.ok) throw new Error(await readError(response));
    const result = await response.json();
    renderUploads(result.items || []);
    listPage = result.pageNumber;
    listTotalPages = Math.max(1, Math.ceil(result.totalCount / result.pageSize));
    listPageLabel.textContent = `Page ${listPage} of ${listTotalPages}`;
    listStatus.textContent = `${result.totalCount} upload${result.totalCount === 1 ? "" : "s"}`;
    listEmpty.hidden = result.items.length !== 0;
    listTable.hidden = result.items.length === 0;
    listPrevious.disabled = listPage <= 1;
    listNext.disabled = listPage >= listTotalPages;
  } catch (error) {
    if (error.name !== "AbortError") listStatus.textContent = error.message || "Uploads could not be loaded.";
  } finally {
    listTable.setAttribute("aria-busy", "false");
  }
}

uploadForm.addEventListener("submit", async event => {
  event.preventDefault();
  const file = uploadFile.files[0];
  if (!file) return showNotice(uploadResult, "Select a JSON file before uploading.", "error");
  if (!file.name.toLowerCase().endsWith(".json")) return showNotice(uploadResult, "The selected file must use the .json extension.", "error");
  if (file.size >= 500000) return showNotice(uploadResult, "The selected file must be smaller than 500,000 bytes.", "error");
  if (!apiKey.value) return showNotice(uploadResult, "Enter the upload API key.", "error");
  if (!uuidPattern.test(idempotencyKey.value)) return showNotice(uploadResult, "Generate a valid idempotency key.", "error");

  const submit = uploadForm.querySelector("button[type='submit']");
  submit.disabled = true;
  showNotice(uploadResult, "Uploading catalog…");
  try {
    const body = new FormData();
    body.append("file", file, file.name);
    const response = await fetch("/api/v1/uploads", {
      method: "POST",
      headers: { "X-Api-Key": apiKey.value, "Idempotency-Key": idempotencyKey.value },
      body
    });
    if (!response.ok) throw new Error(await readError(response));
    const accepted = await response.json();
    const message = document.createDocumentFragment();
    message.append(element("strong", `Accepted ${accepted.fileName}. `), element("span", `Upload ${accepted.uploadId} is ${accepted.status}. `));
    const location = response.headers.get("Location");
    if (location) {
      const link = element("a", "Open live status");
      link.href = location;
      message.append(link);
    }
    uploadResult.replaceChildren(message);
    uploadResult.dataset.kind = "success";
    uploadResult.hidden = false;
    await refreshUuid();
    listPage = 1;
    await loadUploads();
  } catch (error) {
    showNotice(uploadResult, error.message || "The upload could not be completed.", "error");
  } finally {
    submit.disabled = false;
  }
});

document.querySelector("#regenerate-key").addEventListener("click", refreshUuid);
document.querySelector("#copy-key").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(idempotencyKey.value);
    showNotice(uploadResult, "Idempotency key copied.", "success");
  } catch {
    showNotice(uploadResult, "Copy is unavailable. Select and copy the key manually.", "error");
  }
});

listForm.addEventListener("submit", event => { event.preventDefault(); listPage = 1; loadUploads(); });
listForm.addEventListener("change", () => { listPage = 1; });
listPrevious.addEventListener("click", () => { if (listPage > 1) { listPage -= 1; loadUploads(); } });
listNext.addEventListener("click", () => { if (listPage < listTotalPages) { listPage += 1; loadUploads(); } });

function addMetadata(label, value) {
  const container = document.createElement("div");
  container.append(element("dt", label), element("dd", value ?? "—"));
  document.querySelector("#report-metadata").append(container);
}

function addSummaryRow(label, count) {
  const row = document.createElement("tr");
  const heading = element("th", label);
  heading.scope = "row";
  row.append(heading, element("td", String(count)));
  document.querySelector("#report-summary-rows").append(row);
}

function renderReport(report) {
  reportContent.hidden = false;
  reportStatus.hidden = true;
  const metadata = document.querySelector("#report-metadata");
  metadata.replaceChildren();
  addMetadata("File", report.fileName);
  addMetadata("Upload ID", report.uploadId);
  addMetadata("Status", report.status);
  addMetadata("Started", formatDate(report.startedAtUtc));
  addMetadata("Consolidation finished", formatDate(report.consolidationFinishedAtUtc));
  addMetadata("Report generated", formatDate(report.reportGeneratedAtUtc));
  addMetadata("Terminal", formatDate(report.terminalAtUtc));
  addMetadata("SHA-256", report.fileHash);

  const summary = report.summary;
  const total = Math.max(0, summary.received);
  const approvedEnd = total ? (summary.approved / total) * 100 : 0;
  const cleanedEnd = total ? approvedEnd + (summary.cleaned / total) * 100 : 0;
  const chart = document.querySelector("#report-chart");
  chart.style.background = total
    ? `conic-gradient(#1f7a55 0 ${approvedEnd}%, #e59b16 ${approvedEnd}% ${cleanedEnd}%, #c9364f ${cleanedEnd}% 100%)`
    : "#dbeafe";
  chart.setAttribute("aria-label", `Outcomes: ${summary.approved} approved, ${summary.cleaned} cleaned, ${summary.rejected} rejected.`);
  document.querySelector("#report-summary-text").textContent = `${summary.received} received; ${summary.approved} approved, ${summary.cleaned} cleaned, and ${summary.rejected} rejected.`;
  const legend = document.querySelector("#report-legend");
  legend.replaceChildren();
  for (const [label, count, color] of [["Approved", summary.approved, "#1f7a55"], ["Cleaned", summary.cleaned, "#e59b16"], ["Rejected", summary.rejected, "#c9364f"]]) {
    const item = document.createElement("li");
    const swatch = element("span", undefined, "legend-swatch");
    swatch.style.backgroundColor = color;
    item.append(swatch, element("span", `${label}: ${count}`));
    legend.append(item);
  }
  const summaryRows = document.querySelector("#report-summary-rows");
  summaryRows.replaceChildren();
  addSummaryRow("Received", summary.received);
  addSummaryRow("Approved", summary.approved);
  addSummaryRow("Cleaned", summary.cleaned);
  addSummaryRow("Rejected", summary.rejected);

  const itemRows = document.querySelector("#report-items");
  itemRows.replaceChildren();
  for (const item of report.items || []) {
    const row = document.createElement("tr");
    appendCell(row, item.id ?? "—");
    appendCell(row, item.cleanedSellerName ?? item.sellerName ?? "—");
    appendCell(row, item.cleanedName ?? item.name ?? "—");
    appendCell(row, item.cleanedBrand ?? item.brand ?? "—");
    appendCell(row, item.cleanedCategory ?? item.category ?? "—");
    appendCell(row, statusPill(item.status));
    appendCell(row, item.actionTaken);
    itemRows.append(row);
  }
}

async function loadSelectedReport() {
  reportRequest?.abort();
  const uploadId = new URLSearchParams(window.location.search).get("uploadId");
  if (!uploadId) { reportPanel.hidden = true; return; }
  reportPanel.hidden = false;
  reportContent.hidden = true;
  if (!uuidPattern.test(uploadId)) {
    showNotice(reportStatus, "The report URL contains an invalid upload ID.", "error");
    return;
  }
  reportRequest = new AbortController();
  showNotice(reportStatus, "Loading immutable report…");
  try {
    const response = await fetch(`/api/v1/uploads/${encodeURIComponent(uploadId)}/report`, { signal: reportRequest.signal });
    if (response.status === 409) {
      reportStatus.replaceChildren(element("span", "The report is still pending. "));
      const link = element("a", "Open live status");
      link.href = `/api/v1/uploads/${encodeURIComponent(uploadId)}/status`;
      reportStatus.append(link);
      reportStatus.dataset.kind = "info";
      reportStatus.hidden = false;
      return;
    }
    if (!response.ok) throw new Error(await readError(response));
    renderReport(await response.json());
  } catch (error) {
    if (error.name !== "AbortError") showNotice(reportStatus, error.message || "The report could not be loaded.", "error");
  }
}

window.addEventListener("popstate", loadSelectedReport);
refreshUuid();
loadUploads();
loadSelectedReport();
