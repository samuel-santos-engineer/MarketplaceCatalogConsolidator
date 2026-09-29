"use strict";

const form = document.querySelector("#catalog-filters");
const rows = document.querySelector("#catalog-rows");
const status = document.querySelector("#catalog-status");
const empty = document.querySelector("#catalog-empty");
const tableWrap = document.querySelector("#catalog-table-wrap");
const pageLabel = document.querySelector("#catalog-page");
const previous = document.querySelector("#catalog-previous");
const next = document.querySelector("#catalog-next");
let page = 1;
let totalPages = 1;
let activeRequest;

function appendCell(row, value, className) {
  const cell = document.createElement("td");
  cell.textContent = value ?? "—";
  if (className) cell.className = className;
  row.append(cell);
  return cell;
}

function renderProducts(products) {
  rows.replaceChildren();
  for (const product of products) {
    const row = document.createElement("tr");
    appendCell(row, String(product.id));
    appendCell(row, product.name);
    appendCell(row, product.brand);
    appendCell(row, product.category);
    const offersCell = appendCell(row, "");
    if (!product.sellerOffers?.length) {
      offersCell.textContent = "No linked offers";
      offersCell.className = "muted";
    } else {
      const list = document.createElement("ul");
      list.className = "offer-list";
      for (const offer of product.sellerOffers) {
        const item = document.createElement("li");
        const seller = document.createElement("strong");
        seller.textContent = offer.sellerName;
        const source = document.createElement("span");
        source.className = "mono";
        source.textContent = ` — ${offer.sellerProductId}`;
        item.append(seller, source);
        list.append(item);
      }
      offersCell.append(list);
    }
    rows.append(row);
  }
}

function queryParameters() {
  const parameters = new URLSearchParams();
  for (const name of ["category", "brand", "name", "sellerName"]) {
    const value = form.elements[name].value.trim();
    if (value) parameters.set(name, value);
  }
  parameters.set("page", String(page));
  parameters.set("pageSize", form.elements.pageSize.value);
  return parameters;
}

async function readError(response) {
  try {
    const body = await response.json();
    return body.message || `Request failed with status ${response.status}.`;
  } catch {
    return `Request failed with status ${response.status}.`;
  }
}

async function loadCatalog() {
  activeRequest?.abort();
  activeRequest = new AbortController();
  status.textContent = "Loading products…";
  tableWrap.setAttribute("aria-busy", "true");
  previous.disabled = true;
  next.disabled = true;
  try {
    const response = await fetch(`/api/v1/catalog?${queryParameters()}`, { signal: activeRequest.signal });
    if (!response.ok) throw new Error(await readError(response));
    const result = await response.json();
    renderProducts(result.items || []);
    page = result.pageNumber;
    totalPages = Math.max(1, Math.ceil(result.totalCount / result.pageSize));
    pageLabel.textContent = `Page ${page} of ${totalPages}`;
    status.textContent = `${result.totalCount} product${result.totalCount === 1 ? "" : "s"}`;
    empty.hidden = result.items.length !== 0;
    tableWrap.hidden = result.items.length === 0;
    previous.disabled = page <= 1;
    next.disabled = page >= totalPages;
  } catch (error) {
    if (error.name !== "AbortError") {
      rows.replaceChildren();
      empty.hidden = true;
      tableWrap.hidden = false;
      status.textContent = error.message || "The catalog could not be loaded.";
    }
  } finally {
    tableWrap.setAttribute("aria-busy", "false");
  }
}

form.addEventListener("submit", event => { event.preventDefault(); page = 1; loadCatalog(); });
form.addEventListener("reset", () => { page = 1; window.setTimeout(loadCatalog); });
form.addEventListener("change", () => { page = 1; });
previous.addEventListener("click", () => { if (page > 1) { page -= 1; loadCatalog(); } });
next.addEventListener("click", () => { if (page < totalPages) { page += 1; loadCatalog(); } });

loadCatalog();
