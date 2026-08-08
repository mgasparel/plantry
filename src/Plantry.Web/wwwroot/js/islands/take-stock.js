// @ts-check
//
// Take Stock — Walk page island (ADR-020, bead plantry-2zvm.2; redesigned plantry-vvqt).
//
// Buildless Preact + htm + signals. Renders the walk as check-off rows (name, recorded
// amount, a full-height confirm tap target) with all editing depth — stepper, unit,
// reason, expiry, unit conversion, and the lot escape-hatch — moved into a single
// bottom-sheet "adjuster" opened by tapping a row. See .preview/take-stock-walk-redesign.html
// (the approved prototype) for the interaction model this file implements.
//
// ADR-020 §2 boundary: the island holds UI/draft state ONLY — counted value, unit,
// reason, lot draft amounts, confirmed/walk-progress state — plus derived DISPLAY state
// (dirty, down, status, dirtyCount). The server owns reconciliation and persistence. No
// domain math here (§7 tripwire).
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
//     it into the lot-panel placeholder (now inside the adjuster sheet, plantry-vvqt),
//     calls Alpine.initTree, and listens for the lots-dirty-changed / lots-saved events
//     to track dirty state and update its own expandedLots state.
//
// ── Lots ride the page-level Save (plantry-vvqt) ────────────────────────────────
// The lot panel (_LotPanel.cshtml / takeStockLotPanel Alpine component) no longer owns
// a visible Save button or triggers its own POST from a user click — that separate save
// model is exactly what the redesign removes. Its reactive draft state (lots/found,
// setLotAmount/setReason/isDirty) is unchanged and still the source of truth for what's
// pending; the island's own save() calls the SAME `data.save(url)` method the old button
// used to invoke, programmatically, for every expanded+dirty lot panel, as part of one
// page-level Save action. The SaveLots server endpoint is untouched.

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
//   ./toast.js?v=N             bump when toast.js changes
//   ./helpers.js is imported directly by Walk.cshtml with FileVersionProvider, so it
//   gets a content-hash automatically — no manual token needed here.
//
// The convention ensures that a logic-only change (e.g. take-stock-logic.js) is
// caught by bumping the ?v= query, which changes this file's bytes, which changes
// the entry-module content hash, which causes the full dependency graph to reload.

import { render, html, signal, computed } from "./runtime.js?v=1";
import { readHydration, readAntiforgeryToken, postJson } from "./helpers.js";
import {
  setCount, makeRow as makeRowFromSeed, buildSaveItems, reconcileResults, saveStatusMessage,
  mergeSheetUnitIntoRow, shouldShowMarkCounted, rowStatus, toggleRowCheck, confirmRow,
  groupRowsByCategory, readyToSaveCount as computeReadyToSaveCount,
} from "./take-stock-logic.js?v=7";
import { createToast, createToastHost } from "./toast.js?v=1";

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
 * @property {string} saveLotsUrl      URL for POST ?handler=SaveLots (plantry-vvqt)
 * @property {string|null} categoryName
 * @property {number} categorySortOrder
 */

/**
 * One row's reactive state.
 * @typedef {Object} Row
 * @property {string} productId
 * @property {string} productName
 * @property {string} unitCode
 * @property {boolean} hasActiveStock
 * @property {string} lotsUrl
 * @property {string} saveLotsUrl
 * @property {string|null} categoryName
 * @property {number} categorySortOrder
 * @property {UnitOption[]} supportedUnits
 * @property {import("@preact/signals").Signal<number>} recorded
 * @property {import("@preact/signals").Signal<number>} counted
 * @property {import("@preact/signals").Signal<string>} unitId
 * @property {import("@preact/signals").Signal<string>} reason
 * @property {import("@preact/signals").Signal<boolean>} failed
 * @property {import("@preact/signals").Signal<string|null>} failMsg
 * @property {import("@preact/signals").ReadonlySignal<boolean>} dirty
 * @property {import("@preact/signals").ReadonlySignal<boolean>} down
 * @property {import("@preact/signals").Signal<boolean>} confirmed
 * @property {boolean} isNewRow        true for rows injected by inline-add, not in initial hydration
 * @property {import("@preact/signals").Signal<boolean>} needsConversion
 * @property {import("@preact/signals").Signal<string>} convFromUnitId
 * @property {import("@preact/signals").Signal<string>} convFromCode
 * @property {import("@preact/signals").Signal<string>} convToUnitId
 * @property {import("@preact/signals").Signal<string>} convToCode
 * @property {import("@preact/signals").Signal<string>} convFactor
 * @property {import("@preact/signals").Signal<string>} expiryDate   optional yyyy-MM-dd for a found/increased lot (plantry-4onl)
 */

const REASON_LABEL = { Correction: "correction", Consumed: "used it", Discarded: "spoiled" };

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

// Shared toast host bound to this island's own `html` tag function (see toast.js header).
const ToastHost = createToastHost(html);

// ── CountRow component (check-off row, plantry-vvqt) ─────────────────────────────

/** @param {{ row: Row, onOpenSheet: (row:Row)=>void, onToggleCheck: (row:Row)=>void }} props */
function CountRow({ row, onOpenSheet, onToggleCheck }) {
  const status = rowStatus(row);
  const counted = row.counted.value;
  const recorded = row.recorded.value;
  const delta = Math.abs(counted - recorded);
  const down = row.down.value;

  return html`
    <li class=${"ts-checkrow " + status + (row.failed.value ? " errored" : "")}>
      <button type="button" class="ts-checkrow__main" onClick=${() => onOpenSheet(row)}>
        <div class="ts-checkrow__id">
          <div class="ts-checkrow__name">
            ${row.isNewRow && html`<span class="new-tag">New here</span>`}
            <span class="nm-text">${row.productName}</span>
          </div>
          <div class="ts-checkrow__sub">
            ${status === "chg"
              ? html`
                <span class=${"dpill " + (down ? "down" : "up")}>
                  <svg class="icon" aria-hidden="true"><use href=${down ? "#i-minus" : "#i-plus"} /></svg>
                  <span>${delta} ${row.unitCode}</span>
                </span>
                <span class="why">${down ? REASON_LABEL[row.reason.value] ?? "correction" : "found stock"}</span>`
              : status === "ok"
                ? html`<span>Confirmed</span>`
                : row.isNewRow
                  ? html`<span>not stocked at this location yet</span>`
                  : html`<span>${recorded.toLocaleString()} ${row.unitCode} on record</span>`}
            ${row.needsConversion.value && html`
              <span class="ts-checkrow__warn">
                <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> Needs a conversion
              </span>`}
          </div>
        </div>
        <div class="ts-checkrow__qty"><span class="n">${counted.toLocaleString()}</span><span class="u">${row.unitCode}</span></div>
      </button>
      <button type="button" class="ts-checkrow__check" aria-pressed=${status !== "todo"}
              aria-label=${status === "todo" ? "Confirm " + row.productName + " as recorded" : "Un-confirm " + row.productName}
              onClick=${() => onToggleCheck(row)}>
        <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
      </button>

      ${row.failed.value && html`
        <div class="ts-row-err">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
          <span>${row.failMsg.value ?? "Couldn't save"}</span>
        </div>`}
    </li>`;
}

// ── AdjusterSheet component (bottom sheet, plantry-vvqt) ─────────────────────────

/**
 * The sheet's OWN top-level markup (scrim + panel) is always mounted — App never conditionally
 * creates/destroys this component. Open/closed is a CSS ".show" toggle (aria-hidden mirrors it)
 * rather than `row ? html\`...\` : null`, and every row's lot-panel host div is rendered
 * unconditionally too (plantry-vvqt FIX per critic pass 1): a lot-panel host is a CHILD of this
 * always-mounted panel, so closing the sheet (Cancel excepted — see onCancelSheet) never unmounts
 * it, and the Alpine component + its unsaved draft inside survive until an explicit Cancel
 * discards it or a page-level Save flushes it. Only the row-specific chrome (name, stepper,
 * chips, reason/expiry/conversion, the lots-toggle button) is gated on `row` being non-null —
 * that content has nothing to show without an open row and unmounting it costs nothing (it holds
 * no draft state of its own; every field it touches lives on the Row's own signals).
 *
 * @param {{
 *   row: Row | null,
 *   rows: Row[],
 *   expandedLots: import("@preact/signals").Signal<Record<string,boolean>>,
 *   onExpandLots: (row:Row)=>void,
 *   onCollapseLots: (pid:string)=>void,
 *   onAddConversion: (row:Row) => void,
 *   onCancel: () => void,
 *   onDone: () => void,
 * }} props
 */
function AdjusterSheet({ row, rows, expandedLots, onExpandLots, onCollapseLots, onAddConversion, onCancel, onDone }) {
  const open = !!row;
  const counted = row ? row.counted.value : 0;
  const recorded = row ? row.recorded.value : 0;
  const dirty = row ? row.dirty.value : false;
  const down = row ? row.down.value : false;
  const lotsExpanded = row ? (expandedLots.value[row.productId] ?? false) : false;
  const doneLabel = counted === recorded ? "Confirm" : "Done";

  return html`
    <div class=${"ts-adjuster" + (open ? " show" : "")}>
      <div class="ts-adjuster__scrim" onClick=${onCancel}></div>
      <div class="ts-adjuster__panel" role="dialog" aria-modal=${open}
           aria-hidden=${!open}
           aria-label=${open ? "Adjust count for " + row.productName : "Adjust count"}>
        <div class="ts-adjuster__grab"></div>
        <div class="ts-adjuster__body">
          ${open && html`
            <div class="ts-adjuster__name">${row.productName}</div>
            <div class="ts-adjuster__rec">
              ${row.isNewRow
                ? html`<span class="new-tag">New here</span> <span>not stocked at this location yet</span>`
                : html`Plantry has <b>${recorded.toLocaleString()} ${row.unitCode}</b> on record`}
            </div>

            <div class="ts-bigstep">
              <button type="button" class="ts-bigstep__btn" aria-label=${"Decrease count for " + row.productName}
                      onClick=${() => setCount(row, counted - 1)}>
                <svg class="icon" aria-hidden="true"><use href="#i-minus" /></svg>
              </button>
              <div class="ts-bigstep__mid">
                <input type="number" min="0" step="any" value=${counted} inputmode="decimal"
                       aria-label=${"Count for " + row.productName}
                       onInput=${(/** @type {Event} */ e) => setCount(row, /** @type {HTMLInputElement} */ (e.target).value)} />
                ${row.supportedUnits.length > 1
                  ? html`<select class="field__input ts-bigstep__unit-select"
                           aria-label=${"Unit for " + row.productName}
                           value=${row.unitId.value}
                           onChange=${(/** @type {Event} */ e) => { row.unitId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
                      ${row.supportedUnits.map((u) => html`<option value=${u.unitId}>${u.code}</option>`)}
                    </select>`
                  : html`<span class="ts-bigstep__unit">${row.unitCode}</span>`}
              </div>
              <button type="button" class="ts-bigstep__btn" aria-label=${"Increase count for " + row.productName}
                      onClick=${() => setCount(row, counted + 1)}>
                <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>
              </button>
            </div>

            <div class="ts-chiprow">
              <button type="button" class=${"ts-chip" + (counted <= 0 ? " on" : "")}
                      onClick=${() => setCount(row, 0)}>None left</button>
              <button type="button" class=${"ts-chip match" + (counted === recorded ? " on" : "")}
                      onClick=${() => setCount(row, recorded)}>Matches record</button>
            </div>

            ${down && html`
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

            ${dirty && !down && html`
              <div class="ts-expiry">
                <label class="ts-expiry-lbl" for=${"ts-expiry-" + row.productId}>
                  <svg class="icon" aria-hidden="true"><use href="#i-clock" /></svg> When does it expire?
                  <span class="ts-expiry-optional">(optional)</span>
                </label>
                <input type="date" id=${"ts-expiry-" + row.productId} class="field__input ts-expiry-input"
                       value=${row.expiryDate.value}
                       onInput=${(/** @type {Event} */ e) => { row.expiryDate.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
              </div>`}

            ${row.needsConversion.value && html`
              <div class="ts-conversion" role="group" aria-label=${"Conversion factor for " + row.productName}>
                <p class="ts-conversion-lbl">
                  <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
                  How much is 1 ${row.convFromCode.value}? Plantry stocks this in ${row.convToCode.value}.
                </p>
                <div class="ts-conversion-row">
                  <span class="ts-conversion-eq">1 ${row.convFromCode.value} =</span>
                  <input class="field__input ts-conversion-input" type="number" step="any" min="0"
                         placeholder="e.g. 120"
                         aria-label=${"Conversion factor for " + row.productName}
                         value=${row.convFactor.value}
                         onInput=${(/** @type {Event} */ e) => { row.convFactor.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
                  <span class="ts-conversion-unit">${row.convToCode.value}</span>
                  <button type="button" class="btn btn--primary btn--sm" onClick=${() => onAddConversion(row)}>Save conversion</button>
                </div>
              </div>`}

            ${row.hasActiveStock && !row.isNewRow && html`
              <button type="button" class=${"lots-toggle" + (lotsExpanded ? " open" : "")}
                      aria-expanded=${lotsExpanded}
                      aria-controls=${"lot-panel-" + row.productId}
                      onClick=${() => lotsExpanded ? onCollapseLots(row.productId) : onExpandLots(row)}>
                <svg class="icon" aria-hidden="true"><use href="#i-chevron" /></svg>
                Adjust individual lots
              </button>`}
          `}

          ${/* Persistent lot-panel hosts — one per row with active stock, always mounted regardless
               of `open`/`row` so a dirty lot draft survives the sheet closing (plantry-vvqt FIX,
               critic pass 1). Only the currently open+expanded row's host is visible; the rest sit
               hidden, each retaining whatever Alpine draft it was left with. */ ""}
          ${rows.filter((r) => r.hasActiveStock && !r.isNewRow).map((r) => html`
            <div key=${"lot-host-" + r.productId} id=${"lot-panel-" + r.productId} data-product-id=${r.productId}
                 style=${(open && row.productId === r.productId && (expandedLots.value[r.productId] ?? false)) ? "" : "display:none"}>
            </div>`)}
        </div>
        ${open && html`
          <div class="ts-adjuster__foot">
            <button type="button" class="btn btn--ghost" onClick=${onCancel}>Cancel</button>
            <button type="button" class="btn btn--primary" onClick=${onDone}>
              <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> ${doneLabel}
            </button>
          </div>`}
      </div>
    </div>`;
}

// ── App component ────────────────────────────────────────────────────────────────

/**
 * @param {{ rows: import("@preact/signals").Signal<Row[]>,
 *           dirtyCount: import("@preact/signals").ReadonlySignal<number>,
 *           dirtyLotIds: import("@preact/signals").Signal<Record<string,boolean>>,
 *           saving: import("@preact/signals").Signal<boolean>,
 *           toast: import("./toast.js").Toast,
 *           locationName: string,
 *           countedAgo: import("@preact/signals").Signal<string>,
 *           markedThisSession: import("@preact/signals").Signal<boolean>,
 *           sheetRow: import("@preact/signals").Signal<Row|null>,
 *           onSave: () => void,
 *           onOpenAdd: () => void,
 *           onMarkCounted: () => void,
 *           onOpenSheet: (row:Row)=>void,
 *           onToggleCheck: (row:Row)=>void,
 *           onCancelSheet: () => void,
 *           onDoneSheet: () => void,
 *           expandedLots: import("@preact/signals").Signal<Record<string,boolean>>,
 *           onExpandLots: (row:Row) => void,
 *           onCollapseLots: (pid:string) => void,
 *           onAddConversion: (row:Row) => void }} props
 */
function App({
  rows, dirtyCount, dirtyLotIds, saving, toast, locationName, countedAgo, markedThisSession, sheetRow,
  onSave, onOpenAdd, onMarkCounted, onOpenSheet, onToggleCheck, onCancelSheet, onDoneSheet,
  expandedLots, onExpandLots, onCollapseLots, onAddConversion,
}) {
  const allRows = rows.value;
  const mainRows = allRows.filter((r) => !r.isNewRow);
  const addedRows = allRows.filter((r) => r.isNewRow);
  const rowCount = allRows.length;

  const checkedCount = allRows.filter((r) => rowStatus(r) !== "todo").length;
  const uncheckedCount = rowCount - checkedCount;
  const progressPct = rowCount === 0 ? 0 : Math.round((checkedCount / rowCount) * 100);
  const isFullyChecked = rowCount > 0 && checkedCount === rowCount;
  // Combined "ready to save" count for the sticky save bar (take-stock-logic.js readyToSaveCount):
  // dirty rows plus products whose lot panel holds a pending adjustment (plantry-vvqt — lots ride
  // the page Save, so they count as a pending change even though no row-level count changed). This
  // is ALSO the completeness signal "Mark counted" gates on below — a lot-only edit must suppress
  // that button exactly like a dirty row does, since it too is an unsaved change waiting on the
  // Save bar.
  const readyToSaveCount = computeReadyToSaveCount(dirtyCount.value, dirtyLotIds.value);

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
            <div class="sub">${rowCount} product${rowCount === 1 ? "" : "s"} here · ${countedAgo.value}</div>
          </div>
        </div>
        <div class="spacer"></div>
        ${/* Explicit zero-change completion (plantry-hp67) — the Save bar only renders when
             something is ready to save, so a fully-confirmed walk (nothing to change) has no other
             way to advance the location's freshness. An authored tap, not an implicit navigation
             signal (per code review — a back-link stamp fired on any click, reviewed or not).
             Hidden once markedThisSession is true so the header (already updated to "Counted
             today" above) and the button never disagree, and a redundant repeat POST isn't
             one more tap away. */ ""}
        ${shouldShowMarkCounted(readyToSaveCount, rowCount, markedThisSession.value) && html`
          <button type="button" class="btn btn--ghost ts-mark-counted" disabled=${saving.value}
                  onClick=${onMarkCounted}>
            <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
            Mark counted
          </button>`}
      </div>

      ${/* Progress strip (plantry-vvqt) — slim bar + "N of M checked", always visible under the title. */ ""}
      ${rowCount > 0 && html`
        <div class="ts-progress">
          <div class="ts-progress__track"><div class="ts-progress__fill" style=${"width:" + progressPct + "%"}></div></div>
          <span class="ts-progress__lbl">${checkedCount} of ${rowCount} checked</span>
        </div>`}

      <div class="ts-walk-inner">
        <div class="bar-sticky-top ts-add-bar">
          <button type="button" class="ts-add-item" onClick=${onOpenAdd}
                  aria-label="Add a new item to this location">
            <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg> Add item
          </button>
        </div>

        <div class="ts-walk-intro">
          <svg class="icon" aria-hidden="true"><use href="#i-sparkle" /></svg>
          <span>Tap the check if the shelf matches what's on record — tap a row to change the count.</span>
        </div>

        ${rowCount === 0
          ? html`
            <div class="empty-state">
              <div class="empty-state__icon"><svg class="icon" aria-hidden="true"><use href="#i-box" /></svg></div>
              <div class="empty-state__title">Nothing here yet</div>
              <div class="empty-state__body">Add the items on this shelf to start tracking them.</div>
            </div>`
          : groupRowsByCategory(mainRows).map((group) => html`
              <div class="ts-group" key=${group.name}>
                <div class="ts-group__head">${group.name} <span class="n">${group.items.length}</span></div>
                <ul class="ts-rows" role="list">
                  ${group.items.map((row) =>
                    html`<${CountRow} key=${row.productId} row=${row}
                           onOpenSheet=${onOpenSheet} onToggleCheck=${onToggleCheck} />`)}
                </ul>
              </div>`)}

        ${isFullyChecked && html`
          <div class="ts-done">
            <div class="ts-done__big">🎉</div>
            <div class="ts-done__title">${locationName} fully counted</div>
            <div class="ts-done__body">
              ${readyToSaveCount > 0
                ? `${readyToSaveCount} change${readyToSaveCount === 1 ? "" : "s"} ready to save below.`
                : "Everything matched the record — nothing to save."}
            </div>
          </div>`}

        ${addedRows.length > 0 && html`
          <div class="ts-added-head">
            <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>
            <span>Added</span>
          </div>
          <ul class="ts-rows ts-rows--added" role="list" aria-label="Added items">
            ${addedRows.map((row) =>
              html`<${CountRow} key=${row.productId} row=${row}
                     onOpenSheet=${onOpenSheet} onToggleCheck=${onToggleCheck} />`)}
          </ul>`}
      </div>

      ${readyToSaveCount > 0 && html`
        <div class="bar-sticky-bottom">
          <div class="sb-summary">
            <span class="ts-pending-badge">${readyToSaveCount}</span>
            <span><b>${readyToSaveCount}</b> change${readyToSaveCount === 1 ? "" : "s"} ready · ${uncheckedCount} unchecked</span>
          </div>
          <div class="spacer"></div>
          <button type="button" class="btn btn--primary" disabled=${saving.value}
                  onClick=${onSave}>
            <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
            <span>${saving.value ? "Saving…" : "Save"}</span>
          </button>
        </div>`}

      <${AdjusterSheet} row=${sheetRow.value} rows=${allRows} expandedLots=${expandedLots}
        onExpandLots=${onExpandLots} onCollapseLots=${onCollapseLots}
        onAddConversion=${onAddConversion} onCancel=${onCancelSheet} onDone=${onDoneSheet} />

      <${ToastHost} toast=${toast} />
    </div>`;
}

// ── Lot panel flush (plantry-vvqt) ────────────────────────────────────────────

/**
 * Programmatically invokes the SAME `data.save(url)` method the old lot panel's now-removed Save
 * button used to call from a click handler — the panel's reactive draft state (lots/found,
 * isDirty) and its SaveLots POST are unchanged; only the trigger moved. Returns the Alpine
 * component's reactive data object (so callers can re-check `isDirty()` after) or null if there
 * was nothing to flush (no host, no Alpine data, or not dirty).
 *
 * @param {Row} row
 * @returns {Promise<{ data: any, ok: boolean } | null>}
 */
async function flushLotPanel(row) {
  if (!row.saveLotsUrl) return null;
  // The x-data scope is on ".ts-hatch" (_LotPanel.cshtml's root element), injected as a CHILD of
  // the "lot-panel-{productId}" host div — Alpine.$data() walks UP from the element you pass it,
  // so it must be called on .ts-hatch itself, not the host wrapper (which carries no x-data of
  // its own and has no ancestor that does either).
  const host = document.getElementById("lot-panel-" + row.productId)?.querySelector(".ts-hatch");
  const data = host && window.Alpine ? window.Alpine.$data(host) : null;
  if (!data || typeof data.isDirty !== "function" || !data.isDirty()) return null;
  try {
    await data.save(row.saveLotsUrl);
    return { data, ok: !data.isDirty() };
  } catch {
    return { data, ok: false };
  }
}

// ── Save ────────────────────────────────────────────────────────────────────────

/**
 * @param {import("@preact/signals").Signal<Row[]>} rowsSignal
 * @param {string} saveUrl
 * @param {string} token
 * @param {import("./toast.js").Toast} toast
 * @param {import("@preact/signals").Signal<boolean>} saving
 * @param {import("@preact/signals").Signal<string>} countedAgo
 * @param {import("@preact/signals").Signal<boolean>} markedThisSession
 * @param {import("@preact/signals").Signal<Record<string,boolean>>} dirtyLotIds
 */
async function save(rowsSignal, saveUrl, token, toast, saving, countedAgo, markedThisSession, dirtyLotIds) {
  if (saving.value) return;
  const rows = rowsSignal.value;
  const hasDirtyRows = rows.some((r) => r.dirty.value);
  const dirtyLotProductIds = Object.keys(dirtyLotIds.value);

  if (!hasDirtyRows && dirtyLotProductIds.length === 0) return;

  saving.value = true;
  toast.hide();

  // Flush every lot panel still tracked dirty — this IS the page-level Save the ticket describes
  // (plantry-vvqt design point 4): closing the adjuster sheet (Cancel excepted) no longer touches
  // lot draft state at all (the panel's host div stays mounted — see AdjusterSheet — so its Alpine
  // draft survives regardless of which row's sheet is open or closed), so dirtyLotIds accumulates
  // across sheet opens/closes and is only ever cleared here (success) or by an explicit Cancel
  // (discard). This is the single POST trigger for lot adjustments; there is no other.
  //
  // MUST run before the dirty-row snapshot below (plantry-vvqt FIX, critic pass 2): a row that is
  // BOTH scalar-dirty (an edited count) AND lot-dirty (a lot adjustment on the same product) needs
  // its lots-saved handler (which resets row.counted back to row.recorded) to fire first — otherwise
  // buildSaveItems would post the stale pre-flush count, and RecordCountCommand's absolute-delta
  // recompute would silently re-add the units the lot flush just removed.
  let lotSaveFailures = 0;
  for (const productId of dirtyLotProductIds) {
    const row = rows.find((r) => r.productId === productId);
    if (!row) continue;
    const result = await flushLotPanel(row);
    if (result && !result.ok) lotSaveFailures++;
  }

  // Snapshot AFTER the flush loop: a row the lots-saved handler just cleaned (counted reset to
  // recorded) must drop out here rather than post a now-stale count (see the flush-ordering note
  // above).
  const dirty = rows.filter((r) => r.dirty.value);

  if (dirty.length === 0) {
    saving.value = false;
    toast.show(lotSaveFailures > 0
      ? "Some lot changes failed to save — see the highlighted lots."
      : "Lot changes saved.");
    return;
  }

  const items = buildSaveItems(dirty);

  try {
    const resp = await postJson(saveUrl, { items }, token);
    if (!resp.ok) {
      toast.show(saveStatusMessage({ ok: false, status: resp.status }));
      return;
    }

    const data = await resp.json();
    const { saved, failed, needsConversion } = reconcileResults(rows, data.results ?? []);
    // Freshness contract (plantry-hp67): the server reports the completion-stamp outcome in
    // data.countedAgo — a pre-formatted string (from RelativeTimeDisplay via
    // TakeStockDisplay.CountedAgo) only when the stamp actually persisted, null when it failed or
    // didn't run (Walk.cshtml.cs deliberately swallows stamp failures so the per-row results this
    // response rides on still arrive). Updating strictly from the response — never optimistically
    // from saved-row counts — means a silent stamp failure leaves the header honest and keeps the
    // "Mark counted" recovery button available; it also keeps all relative-time wording server-side.
    if (typeof data.countedAgo === "string") {
      countedAgo.value = data.countedAgo;
      markedThisSession.value = true;
    }
    // A lot flush failure (from the loop above) must not be silently overwritten by the row-save
    // outcome below (plantry-vvqt FIX, critic pass 2) — the per-lot inline error markup lives
    // inside a closed, visibility:hidden sheet at this point, so a toast is the only feedback the
    // user gets. Appended to whichever row-save message wins, never dropped.
    const lotSuffix = lotSaveFailures > 0
      ? ` — ${lotSaveFailures} lot change${lotSaveFailures === 1 ? "" : "s"} failed, reopen the row to retry`
      : "";

    // A needsConversion row is neither saved nor a plain failure — it is waiting on a factor.
    // Prompt the user toward the highlighted rows rather than reporting a save success/failure.
    toast.show((needsConversion > 0 && saved === 0 && failed === 0
      ? "Add a conversion factor for the highlighted rows to record them."
      : saveStatusMessage({ ok: true, saved, failed })) + lotSuffix);
  } catch {
    toast.show("Network error — please try again");
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
 *   completeUrl: string,
 *   token: string,
 *   locationName: string,
 *   countedAgo: string,
 * }} config
 */
export function mountTakeStockWalk(root, config) {
  const rowsSignal = signal(config.rows.map(makeRow));
  const saving = signal(false);
  const toast = createToast(signal);
  const expandedLots = signal(/** @type {Record<string,boolean>} */ ({}));
  // Product ids whose lot panel currently holds a pending (unsaved) adjustment — reactive so the
  // save bar and the "ready to save" count can react even though lot draft state itself lives
  // outside the Preact/signals graph (in the Alpine component's own reactive proxy). Driven by the
  // lots-dirty-changed event dispatched from takeStockLotPanel's $watch (Walk.cshtml, plantry-vvqt).
  const dirtyLotIds = signal(/** @type {Record<string,boolean>} */ ({}));
  const dirtyCount = computed(() => rowsSignal.value.filter((r) => r.dirty.value).length);
  // Currently-open adjuster sheet row (plantry-vvqt), or null when the sheet is closed. A snapshot
  // of the row's editable fields is captured on open so Cancel can revert in-sheet edits.
  const sheetRow = signal(/** @type {Row | null} */ (null));
  /** @type {{ counted: number, unitId: string, reason: string, expiryDate: string } | null} */
  let sheetSnapshot = null;
  // Reactive freshness header text (plantry-hp67) — starts at the server-rendered value and is
  // replaced only by a countedAgo string reported in a Save/Complete response (i.e. only when the
  // server confirms the completion stamp persisted), so the header never claims a freshness the
  // database doesn't have.
  const countedAgo = signal(config.countedAgo);
  // True once the server has confirmed a stamp this walk session — hides the "Mark counted"
  // button (via shouldShowMarkCounted) so it can't disagree with the just-updated header.
  const markedThisSession = signal(false);

  // ── beforeunload dirty guard (C7) ─────────────────────
  const guardHandler = (/** @type {BeforeUnloadEvent} */ e) => {
    if ((dirtyCount.value > 0 || Object.keys(dirtyLotIds.value).length > 0) && !saving.value) {
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
    const nextExpanded = { ...expandedLots.value };
    delete nextExpanded[productId];
    expandedLots.value = nextExpanded;
    if (productId in dirtyLotIds.value) {
      const nextDirty = { ...dirtyLotIds.value };
      delete nextDirty[productId];
      dirtyLotIds.value = nextDirty;
    }
  }

  // ── Listen for Alpine lot-panel events ────────────────
  // These events bubble from takeStockLotPanel (Alpine) inside the injected HTML. The panel no
  // longer has its own "Collapse" button (plantry-vvqt — collapsing is now only via the sheet's
  // "Adjust individual lots" toggle, wired to onCollapseLots directly), so there is no longer a
  // collapse-lots dispatch to listen for here.

  // Pending-adjustment tracking (plantry-vvqt) — takeStockLotPanel dispatches this whenever its
  // draft state (lots/found) changes, so the save bar / "ready to save" count can react to a
  // lot-only edit even though no row-level count.value changed.
  window.addEventListener("lots-dirty-changed", (/** @type {Event} */ e) => {
    const detail = /** @type {CustomEvent} */ (e).detail ?? {};
    const pid = detail.productId;
    if (!pid) return;
    const next = { ...dirtyLotIds.value };
    if (detail.dirty) next[pid] = true; else delete next[pid];
    dirtyLotIds.value = next;
  });

  window.addEventListener("lots-saved", (/** @type {Event} */ e) => {
    const pid = /** @type {CustomEvent} */ (e).detail?.productId;
    if (!pid) return;
    const rows = rowsSignal.value;
    const row = rows.find((r) => r.productId === pid);
    if (row) {
      // Reset the scalar count to the recorded value so the row is no longer dirty, and mark it
      // reviewed — a lot save is itself a review of that product (plantry-vvqt).
      row.counted.value = row.recorded.value;
      row.failed.value = false;
      row.failMsg.value = null;
      row.confirmed.value = true;
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
   *   newStapleUnit?: string, newGroupId?: string, newGroupName?: string, newStapleCategoryId?: string,
   *   expiryDate?: string
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
        toast.show("Added — tap Save to record.");
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
          saveLotsUrl: "",
          categoryName: null,
          categorySortOrder: Number.MAX_SAFE_INTEGER,
          supportedUnits: seedUnits,
          isNewRow: true,
          expiryDate: detail.expiryDate ?? "",
        });
        newRow.counted.value = counted;
        rowsSignal.value = [...rows, newRow];
        toast.show("Added — tap Save to record.");
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
        expiryDate:       detail.expiryDate || null,
      };

      try {
        const resp = await postJson(config.addItemUrl, payload, config.token);
        if (!resp.ok) {
          toast.show(`Add item failed (${resp.status}) — please try again`);
          return;
        }

        const data = await resp.json();
        if (!data.isSuccess) {
          toast.show(data.error ?? "Failed to create product");
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
          saveLotsUrl: "",
          categoryName: null,
          categorySortOrder: Number.MAX_SAFE_INTEGER,
          supportedUnits: [],
          isNewRow: true,
        });
        newRow.counted.value = data.countedValue;
        rowsSignal.value = [...rowsSignal.value, newRow];
        toast.show(data.productName + " added" + (data.countedValue > 0 ? " with " + data.countedValue + " " + data.unitCode : "") + ".");
      } catch {
        toast.show("Network error — please try again");
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
      return dirtyCount.value > 0 || Object.keys(dirtyLotIds.value).length > 0;
    },
    /** @returns {number} */
    dirtyCount() {
      return dirtyCount.value;
    },
    /** @param {string} productId */
    openSheet(productId) {
      const row = rowsSignal.value.find((r) => r.productId === productId);
      if (row) openSheet(row);
    },
  };

  const onSave = () => save(rowsSignal, config.saveUrl, config.token, toast, saving, countedAgo, markedThisSession, dirtyLotIds);

  // ── NeedsConversion prompt (plantry-3mwx) ──────────────────────────────
  // Persist the user-supplied factor (1 countedUnit = factor defaultUnit), then re-save so the
  // now-convertible count is recorded. Mirrors the Recipes C10 post-save conversion flow.
  /** @param {Row} row */
  async function addConversion(row) {
    const factor = parseFloat(row.convFactor.value);
    if (!(factor > 0)) {
      toast.show("Enter a conversion factor greater than zero.");
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
        toast.show(`Couldn't save the conversion (${resp.status}) — please try again`);
        return;
      }
      const data = await resp.json();
      if (!data.isSuccess) {
        toast.show(data.error ?? "Couldn't save the conversion.");
        return;
      }
      // Conversion stored — clear the prompt and ensure the row keeps the counted unit, then re-save.
      row.needsConversion.value = false;
      if (row.convFromUnitId.value) row.unitId.value = row.convFromUnitId.value;
      row.convFactor.value = "";
      await save(rowsSignal, config.saveUrl, config.token, toast, saving, countedAgo, markedThisSession, dirtyLotIds);
    } catch {
      toast.show("Network error — please try again");
    }
  }

  const onOpenAdd = () => {
    // Signal the Alpine sheet bridge to open via a window-level event.
    // The bridge listens with x-on:ts-open-add.window, which requires a window dispatch.
    window.dispatchEvent(new CustomEvent("ts-open-add"));
  };

  // ── Adjuster sheet open/cancel/done (plantry-vvqt) ─────────────────────
  // The sheet edits the SAME row signals the old inline controls did (setCount/unitId/reason/
  // expiryDate) — no separate draft object. Cancel restores a snapshot taken at open time so an
  // abandoned edit doesn't leave the row dirty; Done/Confirm just marks the row reviewed and keeps
  // whatever the sheet left behind (a pending edit stays pending — Save still applies it).

  /** @param {Row} row */
  function openSheet(row) {
    sheetSnapshot = {
      counted: row.counted.value,
      unitId: row.unitId.value,
      reason: row.reason.value,
      expiryDate: row.expiryDate.value,
    };
    sheetRow.value = row;
  }

  // Closing the sheet does NOT touch lot draft state (plantry-vvqt FIX, critic pass 1) — the
  // lot-panel host is mounted persistently by AdjusterSheet regardless of which row's sheet is
  // open, so its Alpine draft (and dirtyLotIds tracking) survives a close untouched. A dirty lot
  // panel is only ever resolved by an explicit Cancel (discard, below) or by the page-level Save
  // (flush, in save()) — never as a side effect of Done.
  function closeSheetPanel() {
    sheetRow.value = null;
    sheetSnapshot = null;
  }

  // Cancel reverts the row-level edit AND discards any lot draft opened during this sheet
  // session — both are "undo everything this visit touched", matching the prototype's Cancel.
  function onCancelSheet() {
    const row = sheetRow.value;
    if (row && sheetSnapshot) {
      row.counted.value = sheetSnapshot.counted;
      row.unitId.value = sheetSnapshot.unitId;
      row.reason.value = sheetSnapshot.reason;
      row.expiryDate.value = sheetSnapshot.expiryDate;
      row.failed.value = false;
      row.failMsg.value = null;
    }
    if (row && expandedLots.value[row.productId]) collapseLots(row.productId);
    closeSheetPanel();
  }

  // Done/Confirm marks the row reviewed and closes — it does NOT flush the lot panel (that would
  // put a separate POST trigger right back into the UX, which is exactly what plantry-vvqt design
  // point 4 removes). Any pending lot edit stays pending in dirtyLotIds/expandedLots (both persist
  // across the close, see AdjusterSheet) until the sticky Save bar's Save button flushes it.
  function onDoneSheet() {
    const row = sheetRow.value;
    if (!row) return;
    confirmRow(row);
    closeSheetPanel();
  }

  /** @param {Row} row */
  function onOpenSheet(row) {
    openSheet(row);
  }

  /** @param {Row} row */
  function onToggleCheck(row) {
    toggleRowCheck(row);
    // Force a re-render: rowStatus reads dirty/confirmed signals already tracked by CountRow's own
    // subscriptions, so no extra reassignment is needed here — kept as a named handler for clarity
    // and so E2E/tests have a single call site to reason about.
  }

  // ── Zero-change walk completion (plantry-hp67) ─────────────────────────
  // The Save bar only renders when something is ready to save (see App above), so a walk where
  // every row was already correct never calls save() — leaving the location's freshness stale
  // forever. The "Mark counted" button (shown only when dirtyCount === 0 and there is at least one
  // row) is an explicit, user-authored completion signal — not an implicit stamp on navigation,
  // which would fire on any click regardless of whether the user actually reviewed anything.
  async function onMarkCounted() {
    if (saving.value) return;
    saving.value = true;
    try {
      const resp = await postJson(config.completeUrl, {}, config.token);
      const body = resp.ok ? await resp.json().catch(() => null) : null;
      // Same freshness contract as save() above: the header/button flip ONLY on the
      // server-reported countedAgo string (present exactly when the stamp persisted —
      // OnPostCompleteAsync's failure path is a 500 with no countedAgo), so the UI can never
      // claim a freshness the database doesn't have, and the wording stays server-owned.
      if (body && typeof body.countedAgo === "string") {
        countedAgo.value = body.countedAgo;
        markedThisSession.value = true;
        toast.show("Marked counted.");
      } else {
        toast.show(`Couldn't mark counted (${resp.status}) — please try again`);
      }
    } catch {
      toast.show("Network error — please try again");
    } finally {
      saving.value = false;
    }
  }

  render(
    html`<${App}
      rows=${rowsSignal}
      dirtyCount=${dirtyCount}
      dirtyLotIds=${dirtyLotIds}
      saving=${saving}
      toast=${toast}
      locationName=${config.locationName}
      countedAgo=${countedAgo}
      markedThisSession=${markedThisSession}
      sheetRow=${sheetRow}
      onSave=${onSave}
      onOpenAdd=${onOpenAdd}
      onMarkCounted=${onMarkCounted}
      onOpenSheet=${onOpenSheet}
      onToggleCheck=${onToggleCheck}
      onCancelSheet=${onCancelSheet}
      onDoneSheet=${onDoneSheet}
      expandedLots=${expandedLots}
      onExpandLots=${expandLots}
      onCollapseLots=${collapseLots}
      onAddConversion=${addConversion} />`,
    root,
  );
}
