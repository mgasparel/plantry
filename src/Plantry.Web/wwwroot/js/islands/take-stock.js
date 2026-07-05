// @ts-check
//
// Take Stock — Walk page island (ADR-020, bead plantry-2zvm.2).
//
// Buildless Preact + htm + signals. Replaces the hand-rolled Alpine `takeStockWalk`
// on the real Walk page, rendering the full per-product count rows, inline-add, lot
// escape-hatch, and save-bar.
//
// ADR-020 §2 boundary: the island holds UI/draft state ONLY — counted value, unit,
// reason, lot draft amounts — plus derived DISPLAY state (dirty, down, delta,
// dirtyCount). The server owns reconciliation and persistence. No domain math here
// (§7 tripwire).
//
// ── Bridge decisions (documented here per issue plantry-2zvm.2) ─────────────────
// The shared _ProductSearchCreateSheet partial is Alpine-owned and used by Recipes,
// Shopping, and Take Stock. Porting it here would blast across features. Instead:
//   • The sheet stays in its own thin Alpine context (sheetOpen/draft/closeSheet/
//     saveSheet/selectProduct live on `window.__takeStockSheetBridge`, which the
//     Razor page sets up with Alpine's x-data on the sheet wrapper element only).
//   • The island bridges via two standard DOM events:
//       pick-product   (bubbles from the sheet's <li> click, detail: {value, name, defaultUnitId})
//       ts-sheet-add   (dispatched by the bridge's saveSheet to the island mount element)
//   • takeStockLotPanel stays in Walk.cshtml as a plain JS function so Alpine.initTree
//     can activate it on dynamically injected lot panel HTML (browser doesn't execute
//     scripts in innerHTML fragments — HTML spec §4.12.1).
//   • Lots expansion is managed by the island: it fetches the HTML fragment, injects
//     it into the lot-panel placeholder, calls Alpine.initTree, and listens for the
//     lots-saved / collapse-lots events to update its own expandedLots state.

// ── Cache-busting convention (plantry-hxkf) ───────────────────────────────────
//
// The server (Walk.cshtml) versions this entry module via IFileVersionProvider,
// which appends a content-hash query to this file's URL. Transitive imports of
// runtime.js and take-stock-logic.js are NOT independently versioned by the Razor
// layer — if only a transitive file changes, its URL stays the same and browsers
// serve a stale cached version.
//
// FIX: the ?v= query strings on the import specifiers below ARE the versioning
// mechanism. Changing the query changes the URL the browser uses as a cache key,
// which forces a re-fetch of that module. The content-hash approach (used on this
// file and helpers.js by Razor) cannot be extended to relative specifiers resolved
// inside a JS module — the only option here is a manual version token in the URL.
//
// CONVENTION — when to bump each ?v= query:
//   ./runtime.js?v=N           bump when runtime.js changes (Preact/htm/signals re-exports)
//   ./take-stock-logic.js?v=N  bump when take-stock-logic.js changes
//   ./helpers.js is imported directly by Walk.cshtml with FileVersionProvider, so it
//   gets a content-hash automatically — no manual token needed here.
//
// The convention ensures that a logic-only change (e.g. take-stock-logic.js) is
// caught by bumping the ?v= query, which changes this file's bytes, which changes
// the entry-module content hash, which causes the full dependency graph to reload.

import { render, html, signal, computed } from "./runtime.js?v=1";
import { readHydration, readAntiforgeryToken, postJson } from "./helpers.js";
import { setCount, makeRow as makeRowFromSeed, buildSaveItems, reconcileResults, saveStatusMessage, mergeSheetUnitIntoRow } from "./take-stock-logic.js?v=3";

// ── Types ───────────────────────────────────────────────────────────────────────

/** @typedef {{ unitId: string, code: string }} UnitOption */

/**
 * Hydration shape emitted by the server (Walk.cshtml → IslandRow DTO).
 * @typedef {Object} RowSeed
 * @property {string} productId
 * @property {string} productName
 * @property {number} recorded
 * @property {string} unitCode
 * @property {string} unitId
 * @property {boolean} hasActiveStock
 * @property {string} lotsUrl          URL for GET ?handler=Lots fragment
 * @property {UnitOption[]} [supportedUnits]
 */

/**
 * One row's reactive state.
 * @typedef {Object} Row
 * @property {string} productId
 * @property {string} productName
 * @property {string} unitCode
 * @property {boolean} hasActiveStock
 * @property {string} lotsUrl
 * @property {UnitOption[]} supportedUnits
 * @property {import("@preact/signals").Signal<number>} recorded
 * @property {import("@preact/signals").Signal<number>} counted
 * @property {import("@preact/signals").Signal<string>} unitId
 * @property {import("@preact/signals").Signal<string>} reason
 * @property {import("@preact/signals").Signal<boolean>} failed
 * @property {import("@preact/signals").Signal<string|null>} failMsg
 * @property {import("@preact/signals").ReadonlySignal<boolean>} dirty
 * @property {import("@preact/signals").ReadonlySignal<boolean>} down
 * @property {boolean} isNewRow        true for rows injected by inline-add, not in initial hydration
 * @property {import("@preact/signals").Signal<boolean>} needsConversion
 * @property {import("@preact/signals").Signal<string>} convFromUnitId
 * @property {import("@preact/signals").Signal<string>} convFromCode
 * @property {import("@preact/signals").Signal<string>} convToUnitId
 * @property {import("@preact/signals").Signal<string>} convToCode
 * @property {import("@preact/signals").Signal<string>} convFactor
 */

// ── Row factory ─────────────────────────────────────────────────────────────────

/**
 * Wrap makeRowFromSeed (from take-stock-logic.js) by injecting the real signal/computed
 * factories from the island's runtime. This keeps the logic module free of runtime
 * imports while preserving the injected-factory pattern for testability.
 *
 * @param {RowSeed & { isNewRow?: boolean }} seed @returns {Row}
 */
function makeRow(seed) {
  return makeRowFromSeed(seed, signal, computed);
}

// setCount, buildSaveItems, and reconcileResults are imported from take-stock-logic.js
// and called directly. makeRow is wrapped above to inject the runtime's signal/computed.

// ── CountRow component ───────────────────────────────────────────────────────────

/** @param {{ row: Row, expandedLots: import("@preact/signals").Signal<Record<string,boolean>>, onExpandLots: (row:Row)=>void, onCollapseLots: (pid:string)=>void, onAddConversion: (row:Row)=>void }} props */
function CountRow({ row, expandedLots, onExpandLots, onCollapseLots, onAddConversion }) {
  const counted = row.counted.value;
  const recorded = row.recorded.value;
  const delta = Math.abs(counted - recorded);
  const lotsExpanded = expandedLots.value[row.productId] ?? false;

  return html`
    <li class=${"ts-row" + (row.dirty.value ? " changed" : "") + (row.failed.value ? " errored" : "")}>
      <div class="ts-row-main">
        <div class="ts-id">
          <div class="ts-name">
            ${row.dirty.value && html`<span class="ts-changed-dot"></span>`}
            <span class="nm-text">${row.productName}</span>
          </div>
          ${row.isNewRow
            ? html`<div class="ts-recorded"><span class="new-tag">New here</span> <span>· not stocked at this location yet</span></div>`
            : html`<div class="ts-recorded">Plantry has <b>${recorded.toLocaleString()} ${row.unitCode}</b> on record</div>`}
        </div>

        <div class="ts-count">
          <div class="stepper" role="group">
            <button type="button" class="stepper__btn"
                    aria-label=${"Decrease count for " + row.productName}
                    onClick=${() => setCount(row, counted - 1)}>
              <svg class="icon" aria-hidden="true"><use href="#i-minus" /></svg>
            </button>
            <input class="stepper__val" type="number" min="0" step="any" value=${counted}
                   aria-label=${"Count for " + row.productName}
                   onInput=${(/** @type {Event} */ e) => setCount(row, /** @type {HTMLInputElement} */ (e.target).value)} />
            <button type="button" class="stepper__btn"
                    aria-label=${"Increase count for " + row.productName}
                    onClick=${() => setCount(row, counted + 1)}>
              <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>
            </button>
          </div>

          ${row.supportedUnits.length > 1
            ? html`<select class="field__input" style="min-width:4rem;max-width:7rem"
                     aria-label=${"Unit for " + row.productName}
                     value=${row.unitId.value}
                     onChange=${(/** @type {Event} */ e) => { row.unitId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
                ${row.supportedUnits.map((u) => html`<option value=${u.unitId}>${u.code}</option>`)}
              </select>`
            : html`<span class="ts-unit">${row.unitCode}</span>`}

          <button type="button" class=${"ts-none-btn" + (counted <= 0 ? " on" : "")}
                  aria-label=${"None left — set " + row.productName + " to zero"}
                  onClick=${() => setCount(row, 0)}>None left</button>

          ${row.hasActiveStock && !row.isNewRow && html`
            <button type="button"
                    class=${"ts-expand" + (lotsExpanded ? " open" : "")}
                    aria-label=${"Show lots for " + row.productName}
                    aria-controls=${"lot-panel-" + row.productId}
                    onClick=${() => lotsExpanded ? onCollapseLots(row.productId) : onExpandLots(row)}>
              <svg class="icon" aria-hidden="true"><use href="#i-chevron" /></svg>
            </button>`}
        </div>
      </div>

      ${row.dirty.value && !row.failed.value && html`
        <div class="ts-delta">
          <span class=${"dpill " + (row.down.value ? "down" : "up")}>
            <svg class="icon" aria-hidden="true"><use href=${row.down.value ? "#i-minus" : "#i-plus"} /></svg>
            <span>${delta} ${row.unitCode}</span>
          </span>
          <span>${row.down.value ? "will be removed from stock" : "added to stock"}</span>
        </div>`}

      ${row.down.value && html`
        <div class="ts-reason" role="group" aria-label=${"Reason for " + row.productName + " count change"}>
          <span class="ts-reason-lbl"><svg class="icon" aria-hidden="true"><use href="#i-tag" /></svg> Why the drop?</span>
          <div class="ts-reason-opts">
            ${[["Correction", "Correction"], ["Consumed", "Used it"], ["Discarded", "Spoiled"]].map(
              ([value, label]) => html`
                <button type="button" class=${"ts-reason-opt " + value.toLowerCase() + (row.reason.value === value ? " sel" : "")}
                        onClick=${() => { row.reason.value = value; }}>
                  <span class="rdot"></span> ${label}
                </button>`)}
          </div>
        </div>`}

      ${row.needsConversion.value && html`
        <div class="ts-conversion" role="group"
             aria-label=${"Conversion factor for " + row.productName}>
          <p class="ts-conversion-lbl">
            <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
            No conversion path found. How much is 1 ${row.convFromCode.value}?
          </p>
          <div class="ts-conversion-row">
            <span class="ts-conversion-eq">1 ${row.convFromCode.value} =</span>
            <input class="field__input ts-conversion-input" type="number" step="any" min="0"
                   placeholder="e.g. 120"
                   aria-label=${"Conversion factor for " + row.productName}
                   value=${row.convFactor.value}
                   onInput=${(/** @type {Event} */ e) => { row.convFactor.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
            <span class="ts-conversion-unit">${row.convToCode.value}</span>
            <button type="button" class="btn btn--primary btn--sm"
                    onClick=${() => onAddConversion(row)}>Save conversion</button>
          </div>
        </div>`}

      ${!row.isNewRow && html`
        <div id=${"lot-panel-" + row.productId}
             data-product-id=${row.productId}
             style=${lotsExpanded ? "" : "display:none"}>
        </div>`}

      ${row.failed.value && html`
        <div class="ts-row-err">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
          <span>${row.failMsg.value ?? "Couldn't save"}</span>
        </div>`}
    </li>`;
}

// ── App component ────────────────────────────────────────────────────────────────

/**
 * @param {{ rows: import("@preact/signals").Signal<Row[]>,
 *           dirtyCount: import("@preact/signals").ReadonlySignal<number>,
 *           saving: import("@preact/signals").Signal<boolean>,
 *           toast: import("@preact/signals").Signal<string>,
 *           locationName: string,
 *           onSave: () => void,
 *           onOpenAdd: () => void,
 *           expandedLots: import("@preact/signals").Signal<Record<string,boolean>>,
 *           onExpandLots: (row:Row) => void,
 *           onCollapseLots: (pid:string) => void,
 *           onAddConversion: (row:Row) => void }} props
 */
function App({ rows, dirtyCount, saving, toast, locationName, onSave, onOpenAdd, expandedLots, onExpandLots, onCollapseLots, onAddConversion }) {
  const allRows = rows.value;
  const rowCount = allRows.length;

  return html`
    <div>
      ${/* Walk header */ ""}
      <div class="ts-walk-head">
        <a href="/pantry/take-stock" class="ts-back">
          <svg class="icon" aria-hidden="true"><use href="#i-chevron-right" /></svg> Locations
        </a>
        <div class="ts-walk-title">
          <span class="wt-ico"><svg class="icon" aria-hidden="true"><use href="#i-location" /></svg></span>
          <div>
            <h1>${locationName}</h1>
            <div class="sub">${rowCount} product${rowCount === 1 ? "" : "s"} here</div>
          </div>
        </div>
        <div class="spacer"></div>
      </div>

      <div class="ts-walk-inner">
        <div class="ts-add-bar">
          <button type="button" class="ts-add-item" onClick=${onOpenAdd}
                  aria-label="Add a new item to this location">
            <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg> Add item
          </button>
        </div>

        <div class="ts-walk-intro">
          <svg class="icon" aria-hidden="true"><use href="#i-sparkle" /></svg>
          <span>Counts are pre-filled to what Plantry has on record. Change only what's different — untouched rows are left alone.</span>
        </div>

        ${rowCount === 0
          ? html`
            <div class="ts-empty">
              <div class="em-mark"><svg class="icon" aria-hidden="true"><use href="#i-box" /></svg></div>
              <h3>Nothing here yet</h3>
              <p>Add the items on this shelf to start tracking them.</p>
            </div>`
          : html`
            <ul class="ts-rows" role="list">
              ${allRows.filter(r => !r.isNewRow).map((row) =>
                html`<${CountRow} key=${row.productId} row=${row}
                       expandedLots=${expandedLots}
                       onExpandLots=${onExpandLots}
                       onCollapseLots=${onCollapseLots}
                       onAddConversion=${onAddConversion} />`)}
            </ul>`}

        ${allRows.some(r => r.isNewRow) && html`
          <div class="ts-added-head">
            <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>
            <span>Added</span>
          </div>
          <ul class="ts-rows ts-rows--added" role="list" aria-label="Added items">
            ${allRows.filter(r => r.isNewRow).map((row) =>
              html`<${CountRow} key=${row.productId} row=${row}
                     expandedLots=${expandedLots}
                     onExpandLots=${onExpandLots}
                     onCollapseLots=${onCollapseLots}
                     onAddConversion=${onAddConversion} />`)}
          </ul>`}
      </div>

      ${dirtyCount.value > 0 && html`
        <div class="ts-savebar">
          <div class="sb-summary">
            <span class="ts-pending-badge">${dirtyCount.value}</span>
            <span><b>${dirtyCount.value}</b> ${dirtyCount.value === 1 ? "row" : "rows"} ready to save</span>
          </div>
          <div class="spacer"></div>
          <button type="button" class="btn btn--primary" disabled=${saving.value}
                  onClick=${onSave}>
            <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
            <span>${saving.value ? "Saving…" : "Save"}</span>
          </button>
        </div>`}

      ${toast.value && html`
        <div class="ts-toast" role="status" aria-live="polite" onClick=${() => { toast.value = ""; }}>
          <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
          <span>${toast.value}</span>
        </div>`}
    </div>`;
}

// ── Save ────────────────────────────────────────────────────────────────────────

/**
 * @param {import("@preact/signals").Signal<Row[]>} rowsSignal
 * @param {string} saveUrl
 * @param {string} token
 * @param {import("@preact/signals").Signal<string>} toast
 * @param {import("@preact/signals").Signal<boolean>} saving
 */
async function save(rowsSignal, saveUrl, token, toast, saving) {
  if (saving.value) return;
  const rows = rowsSignal.value;
  const dirty = rows.filter((r) => r.dirty.value);
  if (dirty.length === 0) return;

  saving.value = true;
  toast.value = "";
  const items = buildSaveItems(dirty);

  try {
    const resp = await postJson(saveUrl, { items }, token);
    if (!resp.ok) {
      toast.value = saveStatusMessage({ ok: false, status: resp.status });
      return;
    }

    const data = await resp.json();
    const { saved, failed, needsConversion } = reconcileResults(rows, data.results ?? []);
    // A needsConversion row is neither saved nor a plain failure — it is waiting on a factor.
    // Prompt the user toward the highlighted rows rather than reporting a save success/failure.
    toast.value = needsConversion > 0 && saved === 0 && failed === 0
      ? "Add a conversion factor for the highlighted rows to record them."
      : saveStatusMessage({ ok: true, saved, failed });
  } catch {
    toast.value = "Network error — please try again";
  } finally {
    saving.value = false;
  }
}

// ── Mount ───────────────────────────────────────────────────────────────────────

/**
 * @param {Element} root
 * @param {{
 *   rows: RowSeed[],
 *   saveUrl: string,
 *   addItemUrl: string,
 *   addConversionUrl: string,
 *   token: string,
 *   locationName: string,
 * }} config
 */
export function mountTakeStockWalk(root, config) {
  const rowsSignal = signal(config.rows.map(makeRow));
  const saving = signal(false);
  const toast = signal("");
  const expandedLots = signal(/** @type {Record<string,boolean>} */ ({}));
  const dirtyCount = computed(() => rowsSignal.value.filter((r) => r.dirty.value).length);

  // ── beforeunload dirty guard (C7) ─────────────────────
  const guardHandler = (/** @type {BeforeUnloadEvent} */ e) => {
    if (dirtyCount.value > 0 && !saving.value) {
      e.preventDefault();
      e.returnValue = "";
    }
  };
  window.addEventListener("beforeunload", guardHandler);

  // ── Lot panel helpers ──────────────────────────────────

  /** @param {Row} row */
  async function expandLots(row) {
    const host = document.getElementById("lot-panel-" + row.productId);
    if (!host) return;
    try {
      const resp = await fetch(row.lotsUrl, { headers: { "X-Requested-With": "XMLHttpRequest" } });
      if (!resp.ok) return;
      const lotHtml = await resp.text();
      host.style.display = "";
      host.innerHTML = lotHtml;
      // Re-init Alpine on the injected HTML so takeStockLotPanel activates.
      if (window.Alpine) window.Alpine.initTree(host);
      expandedLots.value = { ...expandedLots.value, [row.productId]: true };
    } catch { /* silently ignore — panel simply doesn't open */ }
  }

  /** @param {string} productId */
  function collapseLots(productId) {
    const host = document.getElementById("lot-panel-" + productId);
    if (host) {
      host.innerHTML = "";
      host.style.display = "none";
    }
    const next = { ...expandedLots.value };
    delete next[productId];
    expandedLots.value = next;
  }

  // ── Listen for Alpine lot-panel events ────────────────
  // These events bubble from takeStockLotPanel (Alpine) inside the injected HTML.
  window.addEventListener("collapse-lots", (/** @type {Event} */ e) => {
    const pid = /** @type {CustomEvent} */ (e).detail?.productId;
    if (pid) collapseLots(pid);
  });

  window.addEventListener("lots-saved", (/** @type {Event} */ e) => {
    const pid = /** @type {CustomEvent} */ (e).detail?.productId;
    if (!pid) return;
    const rows = rowsSignal.value;
    const row = rows.find((r) => r.productId === pid);
    if (row) {
      // Reset the scalar count to the recorded value so the row is no longer dirty.
      row.counted.value = row.recorded.value;
      row.failed.value = false;
      row.failMsg.value = null;
    }
    collapseLots(pid);
  });

  // ── Inline-add sheet bridge ────────────────────────────
  // The shared Alpine sheet dispatches ts-sheet-add from the bridge's saveSheet()
  // on the island's root element. The island handles the payload here.

  /**
   * @param {{
   *   productId?: string, productName?: string, addCount?: number, addUnitId?: string,
   *   addUnitCode?: string, supportedUnits?: UnitOption[], newStapleName?: string,
   *   newStapleUnit?: string, newGroupId?: string, newGroupName?: string, newStapleCategoryId?: string
   * }} detail
   */
  async function handleSheetAdd(detail) {
    if (detail.productId) {
      // Path A: existing product selected — inject as a dirty row.
      const pid = detail.productId;
      const rows = rowsSignal.value;
      const existing = rows.find((r) => r.productId === pid);
      if (existing) {
        // Row already in the working set — merge the sheet-selected count AND unit onto it
        // (pure transform in take-stock-logic.js; plantry-3mwx fix, regression-covered by plantry-1me7).
        mergeSheetUnitIntoRow(existing, detail);
        // supportedUnits/unitCode are plain (non-signal) fields — reassign the array to re-render.
        rowsSignal.value = [...rows];
        toast.value = "Added — tap Save to record.";
      } else {
        // New row not yet in working set.
        const counted = parseFloat(String(detail.addCount ?? 0)) || 0;
        const chosenUnit = detail.addUnitId ?? "";
        const chosenCode = detail.addUnitCode ?? "";
        const seedUnits = detail.supportedUnits
          ?? (chosenUnit && chosenCode ? [{ unitId: chosenUnit, code: chosenCode }] : []);
        const newRow = makeRow({
          productId: pid,
          productName: detail.productName ?? "(new item)",
          recorded: 0,
          unitCode: chosenCode,
          unitId: chosenUnit,
          hasActiveStock: false,
          lotsUrl: "",
          supportedUnits: seedUnits,
          isNewRow: true,
        });
        newRow.counted.value = counted;
        rowsSignal.value = [...rows, newRow];
        toast.value = "Added — tap Save to record.";
      }
    } else if (detail.newStapleName) {
      // Path B: new product (standalone, grouped, or variant) — POST to /AddItem.
      // The handler routes to the right Catalog command based on newGroupId / newGroupName.
      const name   = detail.newStapleName.trim();
      const unitId = detail.newStapleUnit || detail.addUnitId || "";
      if (!name || !unitId) return;

      const counted = parseFloat(String(detail.addCount ?? 0)) || 0;
      const payload = {
        name,
        defaultUnitId:    unitId,
        countedValue:     counted,
        countedUnitId:    detail.addUnitId || unitId,
        // Group-aware fields (plantry-l92u): forwarded to OnPostAddItemAsync for routing.
        newGroupId:       detail.newGroupId       || "",
        newGroupName:     detail.newGroupName      || "",
        categoryId:       detail.newStapleCategoryId || null,
      };

      try {
        const resp = await postJson(config.addItemUrl, payload, config.token);
        if (!resp.ok) {
          toast.value = `Add item failed (${resp.status}) — please try again`;
          return;
        }

        const data = await resp.json();
        if (!data.isSuccess) {
          toast.value = data.error ?? "Failed to create product";
          return;
        }

        const pid = data.productId;
        // Seed the row like the existing-product add path above (recorded 0, counted = the
        // entered quantity) so it renders dirty and a Save button appears (plantry-5os5).
        // /AddItem has already persisted the opening balance, but re-saving is safe:
        // SaveCountsCommand → RecordCountCommand is idempotent by construction (TS-7 — it
        // recomputes `recorded` from current stock and applies an absolute delta, so re-saving
        // the same count yields delta 0 / NoOp). The count is therefore never double-recorded.
        const newRow = makeRow({
          productId: pid,
          productName: data.productName,
          recorded: 0,
          unitCode: data.unitCode,
          unitId: data.unitId,
          hasActiveStock: false,
          lotsUrl: "",
          supportedUnits: [],
          isNewRow: true,
        });
        newRow.counted.value = data.countedValue;
        rowsSignal.value = [...rowsSignal.value, newRow];
        toast.value = data.productName + " added" + (data.countedValue > 0 ? " with " + data.countedValue + " " + data.unitCode : "") + ".";
      } catch {
        toast.value = "Network error — please try again";
      }
    }
  }

  // The island root element listens for ts-sheet-add dispatched by the Alpine bridge.
  root.addEventListener("ts-sheet-add", (/** @type {Event} */ e) => {
    const detail = /** @type {CustomEvent} */ (e).detail ?? {};
    handleSheetAdd(detail);
  });

  // ── E2E / test seam ───────────────────────────────────
  // Expose lightweight imperative API for E2E tests that previously used Alpine.$data().
  window.__takeStockIsland = {
    /** @param {string} productId @param {number} value */
    setCount(productId, value) {
      const row = rowsSignal.value.find((r) => r.productId === productId);
      if (row) setCount(row, value);
    },
    /** @param {number} idx */
    setCountByIndex(idx, value) {
      const rows = rowsSignal.value.filter((r) => !r.isNewRow);
      if (rows[idx]) setCount(rows[idx], value);
    },
    /** @returns {string[]} */
    getProductIds() {
      return rowsSignal.value.filter((r) => !r.isNewRow).map((r) => r.productId);
    },
    /** @param {string} productId @param {string} unitId */
    setUnitId(productId, unitId) {
      const row = rowsSignal.value.find((r) => r.productId === productId);
      if (row) row.unitId.value = unitId;
    },
    /** @returns {boolean} */
    isDirty() {
      return dirtyCount.value > 0;
    },
    /** @returns {number} */
    dirtyCount() {
      return dirtyCount.value;
    },
  };

  const onSave = () => save(rowsSignal, config.saveUrl, config.token, toast, saving);

  // ── NeedsConversion prompt (plantry-3mwx) ──────────────────────────────
  // Persist the user-supplied factor (1 countedUnit = factor defaultUnit), then re-save so the
  // now-convertible count is recorded. Mirrors the Recipes C10 post-save conversion flow.
  /** @param {Row} row */
  async function addConversion(row) {
    const factor = parseFloat(row.convFactor.value);
    if (!(factor > 0)) {
      toast.value = "Enter a conversion factor greater than zero.";
      return;
    }
    try {
      const resp = await postJson(config.addConversionUrl, {
        productId: row.productId,
        fromUnitId: row.convFromUnitId.value,
        toUnitId: row.convToUnitId.value,
        factor,
      }, config.token);
      if (!resp.ok) {
        toast.value = `Couldn't save the conversion (${resp.status}) — please try again`;
        return;
      }
      const data = await resp.json();
      if (!data.isSuccess) {
        toast.value = data.error ?? "Couldn't save the conversion.";
        return;
      }
      // Conversion stored — clear the prompt and ensure the row keeps the counted unit, then re-save.
      row.needsConversion.value = false;
      if (row.convFromUnitId.value) row.unitId.value = row.convFromUnitId.value;
      row.convFactor.value = "";
      await save(rowsSignal, config.saveUrl, config.token, toast, saving);
    } catch {
      toast.value = "Network error — please try again";
    }
  }

  const onOpenAdd = () => {
    // Signal the Alpine sheet bridge to open via a window-level event.
    // The bridge listens with x-on:ts-open-add.window, which requires a window dispatch.
    window.dispatchEvent(new CustomEvent("ts-open-add"));
  };

  render(
    html`<${App}
      rows=${rowsSignal}
      dirtyCount=${dirtyCount}
      saving=${saving}
      toast=${toast}
      locationName=${config.locationName}
      onSave=${onSave}
      onOpenAdd=${onOpenAdd}
      expandedLots=${expandedLots}
      onExpandLots=${expandLots}
      onCollapseLots=${collapseLots}
      onAddConversion=${addConversion} />`,
    root,
  );
}
