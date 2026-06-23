// @ts-check
//
// Take Stock — count-rows island (ADR-020 proof, bead plantry-2zvm).
//
// Buildless Preact + htm + signals. Renders the per-product count rows, derives
// dirty/down/delta state via `computed` signals, and POSTs the dirty set to the
// server's existing JSON Save handler (WalkModel.OnPostSaveAsync — unchanged).
//
// ADR-020 §2 boundary: this island holds UI/draft state ONLY — the counted
// value, the chosen unit, the reason — plus derived DISPLAY state (dirty, down,
// delta, dirtyCount). The server owns reconciliation and persistence; on save it
// returns the authoritative per-row result and the island reflects it. No domain
// math lives here (§7 tripwire).
//
// Compare wwwroot/js/.../Walk.cshtml's hand-rolled `takeStockWalk`: the manual
// dirty bookkeeping (`row.dirty = counted !== recorded` set in every mutator) is
// replaced by ONE `computed` that cannot drift.

// Runtime imports resolve by relative path — no import map. (The JSDoc `import("@preact/signals")`
// type references below are checker-only and resolve via jsconfig `paths` → vendor/vendor.d.ts.)
import { h, render } from "./vendor/preact.module.js";
import { signal, computed } from "./vendor/signals.module.js";
import htm from "./vendor/htm.module.js";

const html = htm.bind(h);

/** @typedef {{ unitId: string, code: string }} UnitOption */

/**
 * Hydration shape emitted by the server (mirror of WalkModel.AlpineRow + the
 * product name/recorded the island needs because it renders the whole row).
 * @typedef {Object} RowSeed
 * @property {string} productId
 * @property {string} productName
 * @property {number} recorded
 * @property {string} unitCode
 * @property {string} unitId
 * @property {UnitOption[]} [supportedUnits]
 */

/**
 * One row's reactive state. `recorded`/`counted`/`unitId`/`reason`/`failed` are
 * signals; `dirty`/`down` are derived and recompute automatically — including
 * after a successful save bumps `recorded` to the new server truth.
 * @typedef {Object} Row
 * @property {string} productId
 * @property {string} productName
 * @property {string} unitCode
 * @property {UnitOption[]} supportedUnits
 * @property {import("@preact/signals").Signal<number>} recorded
 * @property {import("@preact/signals").Signal<number>} counted
 * @property {import("@preact/signals").Signal<string>} unitId
 * @property {import("@preact/signals").Signal<string>} reason
 * @property {import("@preact/signals").Signal<boolean>} failed
 * @property {import("@preact/signals").Signal<string|null>} failMsg
 * @property {import("@preact/signals").ReadonlySignal<boolean>} dirty
 * @property {import("@preact/signals").ReadonlySignal<boolean>} down
 */

/** @param {RowSeed} seed @returns {Row} */
function makeRow(seed) {
  const recorded = signal(seed.recorded);
  const counted = signal(seed.recorded);
  const dirty = computed(() => counted.value !== recorded.value);
  const down = computed(() => dirty.value && counted.value < recorded.value);
  return {
    productId: seed.productId,
    productName: seed.productName,
    unitCode: seed.unitCode,
    supportedUnits: seed.supportedUnits ?? [],
    recorded,
    counted,
    unitId: signal(seed.unitId),
    reason: signal("Correction"),
    failed: signal(false),
    failMsg: signal(/** @type {string | null} */ (null)),
    dirty,
    down,
  };
}

/** @param {Row} row @param {string | number} raw */
function setCount(row, raw) {
  const parsed = typeof raw === "number" ? raw : parseFloat(raw);
  row.counted.value = Number.isNaN(parsed) ? row.recorded.value : Math.max(0, parsed);
  row.failed.value = false;
  row.failMsg.value = null;
}

// ── Components ──────────────────────────────────────────────────────────────

/** @param {{ row: Row }} props */
function CountRow({ row }) {
  const counted = row.counted.value;
  const recorded = row.recorded.value;
  const delta = Math.abs(counted - recorded);
  return html`
    <li class=${"ts-row" + (row.dirty.value ? " changed" : "") + (row.failed.value ? " errored" : "")}>
      <div class="ts-row-main">
        <div class="ts-id">
          <div class="ts-name">
            ${row.dirty.value && html`<span class="ts-changed-dot"></span>`}
            <span class="nm-text">${row.productName}</span>
          </div>
          <div class="ts-recorded">Plantry has <b>${recorded} ${row.unitCode}</b> on record</div>
        </div>

        <div class="ts-count">
          <div class="stepper" role="group">
            <button type="button" class="stepper__btn" aria-label=${"Decrease count for " + row.productName}
                    onClick=${() => setCount(row, counted - 1)}>
              <svg class="icon" aria-hidden="true"><use href="#i-minus" /></svg>
            </button>
            <input class="stepper__val" type="number" min="0" step="any" value=${counted}
                   aria-label=${"Count for " + row.productName}
                   onInput=${(/** @type {Event} */ e) => setCount(row, /** @type {HTMLInputElement} */ (e.target).value)} />
            <button type="button" class="stepper__btn" aria-label=${"Increase count for " + row.productName}
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

      ${row.failed.value && html`
        <div class="ts-row-err">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
          <span>${row.failMsg.value ?? "Couldn't save"}</span>
        </div>`}
    </li>`;
}

/**
 * @param {{ rows: Row[], dirtyCount: import("@preact/signals").ReadonlySignal<number>,
 *           saving: import("@preact/signals").Signal<boolean>,
 *           toast: import("@preact/signals").Signal<string>, onSave: () => void }} props
 */
function App({ rows, dirtyCount, saving, toast, onSave }) {
  return html`
    <div class="ts-walk-inner">
      <div class="ts-walk-intro">
        <svg class="icon" aria-hidden="true"><use href="#i-sparkle" /></svg>
        <span>Counts are pre-filled to what Plantry has on record. Change only what's different — untouched rows are left alone.</span>
      </div>

      <ul class="ts-rows" role="list">
        ${rows.map((row) => html`<${CountRow} key=${row.productId} row=${row} />`)}
      </ul>

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

// ── Save (mutation; server stays authoritative) ─────────────────────────────

/**
 * @param {Row[]} rows
 * @param {string} saveUrl
 * @param {string} token
 * @param {import("@preact/signals").Signal<string>} toast
 * @param {import("@preact/signals").Signal<boolean>} saving
 */
async function save(rows, saveUrl, token, toast, saving) {
  if (saving.value) return;
  const dirty = rows.filter((r) => r.dirty.value);
  if (dirty.length === 0) return;

  saving.value = true;
  toast.value = "";
  const items = dirty.map((r) => ({
    productId: r.productId,
    countedValue: r.counted.value,
    countedUnitId: r.unitId.value,
    reason: r.reason.value,
  }));

  try {
    const resp = await fetch(saveUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "RequestVerificationToken": token,
        "X-Requested-With": "XMLHttpRequest",
      },
      body: JSON.stringify({ items }),
    });
    if (!resp.ok) {
      toast.value = `Save failed (${resp.status}) — please try again`;
      return;
    }

    const data = await resp.json();
    const byId = new Map(rows.map((r) => [r.productId, r]));
    let saved = 0;
    let failed = 0;
    for (const result of data.results ?? []) {
      const row = byId.get(result.productId);
      if (!row) continue;
      if (result.isSuccess) {
        row.recorded.value = row.counted.value; // adopt server truth → dirty recomputes to false
        row.failed.value = false;
        row.failMsg.value = null;
        saved++;
      } else {
        row.failed.value = true;
        row.failMsg.value = result.error ?? "Failed to save";
        failed++;
      }
    }

    toast.value =
      failed === 0
        ? saved === 1 ? "1 item updated" : `${saved} items updated`
        : saved === 0 ? "Save failed — please try again"
        : `${saved} saved, ${failed} failed — retry the highlighted rows`;
  } catch {
    toast.value = "Network error — please try again";
  } finally {
    saving.value = false;
  }
}

// ── Mount ───────────────────────────────────────────────────────────────────

/**
 * @param {Element} root
 * @param {{ rows: RowSeed[], saveUrl: string, token: string }} config
 */
export function mountTakeStock(root, config) {
  const rows = config.rows.map(makeRow);
  const saving = signal(false);
  const toast = signal("");
  const dirtyCount = computed(() => rows.filter((r) => r.dirty.value).length);
  const onSave = () => save(rows, config.saveUrl, config.token, toast, saving);

  render(
    html`<${App} rows=${rows} dirtyCount=${dirtyCount} saving=${saving} toast=${toast} onSave=${onSave} />`,
    root,
  );
}
