// @ts-check
//
// Intake Review island (ADR-020, bead plantry-2zvm.3).
//
// Buildless Preact + htm + signals. Replaces the htmx OOB-bundle machinery on the
// Intake review form (ADR-013 is retired for this surface — a reactive runtime makes
// "derived view drifts after a localized mutation" structurally impossible).
//
// ADR-020 §2 boundary:
//   SERVER: domain rules (prefill priority chain, validation, aggregates after save),
//           persistence, commit/discard redirect target.
//   ISLAND: UI/draft state (drawer open/closed, form field values), derived DISPLAY
//           state (chips, progress, commit bar, receipt total) computed via signals
//           from the authoritative line states returned by each JSON endpoint.
//
// Boundary judgment calls (ADR-020 §3, preserved verbatim from the issue):
//   1. ComputePrefill priority chain STAYS server-side. The island receives computed
//      prefill VALUES — it never re-runs the chain.
//   2. On product re-selection the island fills empty unit/location/expiry from
//      hydrated product defaults (form-filling from held data = UI, allowed).
//      It does NOT own the priority chain.
//   3. Validation is mirrored client-side for instant feedback; server re-validates
//      and is authoritative. Every save response overwrites client-derived display.

// ── Cache-busting convention (plantry-hxkf) ───────────────────────────────────
//
// The server (Review.cshtml) versions this entry module via IFileVersionProvider,
// which appends a content-hash query to this file's URL. Transitive imports of
// runtime.js and intake-review-logic.js are NOT independently versioned by the Razor
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
//   ./runtime.js?v=N              bump when runtime.js changes (Preact/htm/signals re-exports)
//   ./intake-review-logic.js?v=N  bump when intake-review-logic.js changes
//   ./helpers.js is imported directly by Review.cshtml with FileVersionProvider, so it
//   gets a content-hash automatically — no manual token needed here.
//
// The convention ensures that a logic-only change (e.g. intake-review-logic.js) is
// caught by bumping the ?v= query, which changes this file's bytes, which changes
// the entry-module content hash, which causes the full dependency graph to reload.

import { render, html, signal, computed, batch } from "./runtime.js?v=1";
import { readHydration, readAntiforgeryToken, postJson } from "./helpers.js";
import {
  makeLine as makeLineFromSeed,
  lineSection,
  isUnmatched,
  buildSaveLineBody,
  commitBarCounts,
  estimateHint,
  decisionVariant,
  questionCopy,
  firstNeedsLineId,
} from "./intake-review-logic.js?v=2";

// ── Type documentation ───────────────────────────────────────────────────────

/**
 * @typedef {Object} PrefillData
 * @property {string|null} productId
 * @property {string|null} productName
 * @property {number|null} quantity
 * @property {string|null} unitId
 * @property {string|null} locationId
 * @property {number|null} price
 * @property {string|null} expiry  ISO date string yyyy-MM-dd or null
 * @property {string|null} skuId
 */

/**
 * @typedef {Object} LineSeed
 * @property {string} lineId
 * @property {string} receiptText
 * @property {string} confidence   "High" | "Low" | "None"
 * @property {string} status       "Pending" | "Confirmed" | "Dismissed" | "Committed"
 * @property {string|null} productId
 * @property {string|null} skuId
 * @property {number|null} quantity
 * @property {string|null} unitId
 * @property {string|null} locationId
 * @property {string|null} expiryDate
 * @property {number|null} price
 * @property {boolean} isNewProduct
 * @property {string|null} newProductName
 * @property {string|null} newProductCategoryId
 * @property {number|null} suggestedPrice
 */

/**
 * @typedef {Object} SkuOption
 * @property {string} id
 * @property {string} label
 */

/**
 * @typedef {Object} ProductDefaults
 * @property {string} unitId
 * @property {string|null} locationId
 * @property {string|null} expiry    ISO date string yyyy-MM-dd or null
 */

/**
 * @typedef {Object} ProductHydration
 * @property {string} id
 * @property {string} name
 * @property {SkuOption[]} skus
 * @property {ProductDefaults} defaults
 */

/**
 * @typedef {Object} UnitHydration
 * @property {string} id
 * @property {string} code
 * @property {string} name
 */

/**
 * @typedef {Object} LocationHydration
 * @property {string} id
 * @property {string} name
 */

/**
 * @typedef {Object} CategoryHydration
 * @property {string} id
 * @property {string} name
 * @property {number|null} hue
 */

/**
 * @typedef {Object} AlternativeHydration
 * @property {string} productId
 * @property {string} productName
 * @property {number} confidence
 */

/**
 * Weight→each estimate affordance (plantry-1mu). Display-only: the drawer renders it as a hint
 * ("~7 each estimated from 1.34 lb"). Whether the each-count is pre-filled is decided server-side.
 * @typedef {Object} EstimateHydration
 * @property {number} eachCount
 * @property {number} weight
 * @property {string} weightUnit
 * @property {string} confidence   "High" | "Low"
 */

/**
 * @typedef {Object} SessionHydration
 * @property {string} merchantText
 * @property {string} sessionDate
 * @property {string} today        ISO date string yyyy-MM-dd
 * @property {string} commitUrl
 * @property {string} discardUrl
 * @property {string} saveLineUrl
 * @property {string} dismissLineUrl
 * @property {string} restoreLineUrl
 * @property {string} reopenLineUrl
 * @property {ProductHydration[]} products
 * @property {UnitHydration[]} units
 * @property {LocationHydration[]} locations
 * @property {CategoryHydration[]} categories
 * @property {Array<{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null, estimate: EstimateHydration|null}>} lines
 * @property {string} scanVia            "photo" | "email" — drives the rcpt-meta-top via tag
 * @property {string} scannedLabel       relative "scanned …" copy for rcpt-meta-top
 * @property {string|null} storeBranch   branch/location line under the store name
 * @property {string|null} purchaseDate  formatted receipt date, or null
 * @property {string|null} purchaseTime  formatted receipt time, or null
 * @property {number|null} subtotal
 * @property {number|null} tax
 * @property {number|null} total         parsed receipt total (falls back to the computed sum when null)
 * @property {string|null} payment       payment/tender descriptor line
 * @property {string|null} receiptNo     receipt / transaction number
 */

/**
 * @typedef {Object} LineState
 * @property {string} lineId
 * @property {string} receiptText
 * @property {string} confidence
 * @property {import("@preact/signals").Signal<string>} status   Pending|Confirmed|Dismissed|Committed
 * @property {boolean} isNewProduct  — set after a successful save
 * @property {string|null} newProductName
 * @property {import("@preact/signals").Signal<number|null>} price   effective price (user-set or suggested)
 * @property {import("@preact/signals").Signal<number|null>} suggestedPrice
 * @property {import("@preact/signals").Signal<boolean>} saving
 * @property {import("@preact/signals").Signal<string|null>} error
 * @property {import("@preact/signals").Signal<boolean>} drawerOpen
 * @property {import("@preact/signals").Signal<boolean>} searchOpen  product dropdown open/closed
 * // Form draft — signals so the drawer can be reactive
 * @property {import("@preact/signals").Signal<boolean>} createNew
 * @property {import("@preact/signals").Signal<string>} draftProductId
 * @property {import("@preact/signals").Signal<string>} draftProductName  the text input value
 * @property {import("@preact/signals").Signal<string>} draftSkuId
 * @property {import("@preact/signals").Signal<string>} draftQty
 * @property {import("@preact/signals").Signal<string>} draftUnitId
 * @property {import("@preact/signals").Signal<string>} draftLocationId
 * @property {import("@preact/signals").Signal<string>} draftExpiry
 * @property {import("@preact/signals").Signal<string>} draftExpiryMode   "has" | "never"
 * @property {import("@preact/signals").Signal<string>} draftPrice
 * @property {import("@preact/signals").Signal<string>} draftNewName
 * @property {import("@preact/signals").Signal<string>} draftNewCategoryId
 * @property {AlternativeHydration[]|null} alternatives
 * @property {EstimateHydration|null} estimate
 */

// ── Line factory ─────────────────────────────────────────────────────────────
//
// makeLine, lineSection, isUnmatched, and buildSaveLineBody are pure transforms
// that live in intake-review-logic.js (imported above) so they can be unit-tested
// with `node --test` without browser globals. The island calls them by passing
// the real `signal` factory from runtime.js.

/**
 * @param {{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null, estimate: EstimateHydration|null}} seed
 * @returns {LineState}
 */
function makeLine(seed) {
  return makeLineFromSeed(seed, signal);
}

// ── ProductSearch component ──────────────────────────────────────────────────

/**
 * Posts a save-line body and applies the result to the line's signals. The single SaveLine submit
 * path — shared by the focused exception's "Confirm & next" and a ready row's edit save: on failure it
 * sets the error and reopens the drawer; on success it clears the error and calls onSaved; it always
 * clears `saving` in finally. (Callers own validation + body construction via buildSaveLineBody.)
 * @param {LineState} ls
 * @param {object} body
 * @param {string} saveLineUrl
 * @param {string} token
 * @param {(ls: LineState, data: any) => void} onSaved
 */
async function submitSaveLine(ls, body, saveLineUrl, token, onSaved) {
  try {
    const resp = await postJson(saveLineUrl, body, token);
    const data = await resp.json();
    if (!resp.ok || data.error) {
      ls.error.value = data.error ?? `Save failed (${resp.status})`;
      ls.drawerOpen.value = true;
    } else {
      ls.error.value = null;
      onSaved(ls, data);
    }
  } catch {
    ls.error.value = "Network error — please try again.";
    ls.drawerOpen.value = true;
  } finally {
    ls.saving.value = false;
  }
}

/**
 * @param {{
 *   ls: LineState,
 *   products: ProductHydration[],
 *   listboxId: string,
 * }} props
 */
function ProductSearch({ ls, products, listboxId }) {
  // searchOpen lives in LineState so it persists across re-renders (signals created in
  // function bodies are re-created on every call, discarding state).
  const searchOpen = ls.searchOpen;
  const query = ls.draftProductName;

  const filtered = computed(() => {
    const q = query.value.trim().toLowerCase();
    if (!q) return products.slice(0, 20);
    return products.filter((p) => p.name.toLowerCase().includes(q)).slice(0, 20);
  });

  /** @param {string} id @param {string} name */
  function selectProduct(id, name) {
    ls.draftProductId.value = id;
    ls.draftProductName.value = name;
    ls.draftSkuId.value = "";
    searchOpen.value = false;
    // Fill empty unit/location/expiry from product defaults (Boundary judgment call 2).
    const product = products.find((p) => p.id === id);
    if (product) {
      if (!ls.draftUnitId.value) {
        ls.draftUnitId.value = product.defaults.unitId;
      }
      if (!ls.draftLocationId.value && product.defaults.locationId) {
        ls.draftLocationId.value = product.defaults.locationId;
      }
      if (ls.draftExpiryMode.value === "never" && product.defaults.expiry) {
        ls.draftExpiry.value = product.defaults.expiry;
        ls.draftExpiryMode.value = "has";
      }
    }
  }

  const skus = computed(() => {
    const pid = ls.draftProductId.value;
    if (!pid) return [];
    const product = products.find((p) => p.id === pid);
    return product?.skus ?? [];
  });

  return html`
    <div class="form-grid__field form-grid__field--full" style="display: ${ls.createNew.value ? 'none' : ''}">
      <label class="form-grid__field__label">Product</label>
      <div class="form-grid__field__control">
        <div class="searchable-select">
          <input type="hidden" name="Edit.ProductId" value=${ls.draftProductId} />
          <div class="searchable-select__control">
            <input type="text" class="field__input" role="combobox"
                   aria-controls=${listboxId}
                   aria-autocomplete="list"
                   autocomplete="off"
                   placeholder="Find a product…"
                   value=${ls.draftProductName}
                   aria-expanded=${String(searchOpen.value)}
                   onFocus=${() => { searchOpen.value = true; }}
                   onInput=${(/** @type {InputEvent} */ e) => {
                     ls.draftProductName.value = /** @type {HTMLInputElement} */ (e.target).value;
                     ls.draftProductId.value = "";
                     searchOpen.value = true;
                   }}
                   onKeyDown=${(/** @type {KeyboardEvent} */ e) => {
                     if (e.key === "Escape") { searchOpen.value = false; e.preventDefault(); }
                   }} />
          </div>
          ${searchOpen.value && html`
            <ul class="searchable-select__listbox" id=${listboxId} role="listbox">
              ${filtered.value.map((p) => html`
                <li key=${p.id} role="option" data-value=${p.id}
                    onMouseDown=${(/** @type {MouseEvent} */ e) => { e.preventDefault(); selectProduct(p.id, p.name); }}>
                  ${p.name}
                </li>
              `)}
            </ul>
          `}
        </div>
      </div>
    </div>

    ${skus.value.length > 0 && !ls.createNew.value && html`
      <div class="form-grid__field form-grid__field--full">
        <label class="form-grid__field__label">Pack size (optional)</label>
        <div class="form-grid__field__control">
          <select class="field__input" name="Edit.SkuId"
                  value=${ls.draftSkuId}
                  onChange=${(/** @type {Event} */ e) => { ls.draftSkuId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
            <option value="">— Any size —</option>
            ${skus.value.map((s) => html`<option key=${s.id} value=${s.id}>${s.label}</option>`)}
          </select>
        </div>
      </div>
    `}
  `;
}

// ── AlternativesStrip component ──────────────────────────────────────────────
//
// "Did you mean" suggestion bar — shown when a line has alternatives and the
// user has not yet switched to "create new". Selecting an alternative fills the
// product/name/sku drafts; the ranking label ("best" / "N%") comes from the
// hydration confidence score.

/**
 * Selects the nth (0-based) alternative for a line, mirroring a suggest-opt click. Shared by the
 * click handler and the focused exception's 1–9 keyboard shortcut (plantry-15l3).
 * @param {LineState} ls @param {number} k
 */
function pickAlternative(ls, k) {
  const alt = ls.alternatives?.[k];
  if (!alt) return;
  batch(() => {
    ls.draftProductId.value = alt.productId;
    ls.draftProductName.value = alt.productName;
    ls.draftSkuId.value = "";
  });
}

/**
 * @param {{
 *   ls: LineState,
 *   showKbd?: boolean,
 * }} props
 */
function AlternativesStrip({ ls, showKbd = false }) {
  if (!ls.alternatives || ls.createNew.value) return null;
  return html`
    <div class="match-suggest">
      <div class="match-suggest__label">
        Did you mean — <span>several products share this name</span>
      </div>
      <div class="suggest-opts">
        ${ls.alternatives.map((alt, i) => {
          const rankLabel = i === 0 ? "best" : `${Math.round(alt.confidence * 100)}%`;
          const selected = ls.draftProductId.value === alt.productId;
          return html`
            <button key=${alt.productId} type="button"
                    class=${"suggest-opt" + (selected ? " sel" : "")}
                    onClick=${() => pickAlternative(ls, i)}>
              ${selected && html`<svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>`}
              ${alt.productName}
              ${showKbd && i < 9 && html`<kbd>${i + 1}</kbd>`}
              <span class="rk">${rankLabel}</span>
            </button>
          `;
        })}
      </div>
    </div>
  `;
}

// ── ExpiryPriceFields component ───────────────────────────────────────────────
//
// The expiry/date cluster (date input + Date|Never segmented control) together
// with the optional price field. These three fields form the "when did it expire
// and what did it cost" metadata cluster and always appear or disappear as a
// unit relative to priceReadOnly — extracting them makes ReviewDrawer's grid
// easier to scan.

/**
 * @param {{
 *   ls: LineState,
 *   units: UnitHydration[],
 *   today: string,
 *   priceReadOnly: boolean,
 * }} props
 */
function ExpiryPriceFields({ ls, units, today, priceReadOnly }) {
  return html`
    <div class="form-grid__field">
      <label class="form-grid__field__label">Expires</label>
      <div class="form-grid__field__control">
        <div class="expiry-field">
          ${ls.draftExpiryMode.value === "has" && html`
            <input type="date" class="field__input expiry-field__date"
                   name="Edit.ExpiryDate"
                   value=${ls.draftExpiry}
                   onInput=${(/** @type {InputEvent} */ e) => { ls.draftExpiry.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
          `}
          <div class="seg-ctrl" role="group" aria-label="Expiry">
            <label class="seg-ctrl__item">
              <input type="radio" name=${`expiry-mode-${ls.lineId}`} value="has"
                     checked=${ls.draftExpiryMode.value === "has"}
                     onChange=${() => {
                       ls.draftExpiryMode.value = "has";
                       if (!ls.draftExpiry.value) ls.draftExpiry.value = today;
                     }} />
              <span>Date</span>
            </label>
            <label class="seg-ctrl__item">
              <input type="radio" name=${`expiry-mode-${ls.lineId}`} value="never"
                     checked=${ls.draftExpiryMode.value === "never"}
                     onChange=${() => { ls.draftExpiryMode.value = "never"; }} />
              <span>Never</span>
            </label>
          </div>
        </div>
      </div>
    </div>

    <div class="form-grid__field" style=${`grid-column: ${priceReadOnly ? "span 4" : "span 2"}`}>
      <label class="form-grid__field__label">Unit</label>
      <div class="form-grid__field__control">
        <select class="field__input" name="Edit.UnitId"
                value=${ls.draftUnitId}
                onChange=${(/** @type {Event} */ e) => { ls.draftUnitId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
          <option value="">— Unit —</option>
          ${units.map((u) => html`<option key=${u.id} value=${u.id}>${u.code} — ${u.name}</option>`)}
        </select>
      </div>
    </div>

    ${!priceReadOnly && html`
      <div class="form-grid__field" style="grid-column: span 2">
        <label class="form-grid__field__label">Price (optional)</label>
        <div class="form-grid__field__control">
          <input class="field__input" type="number" step="any" min="0"
                 name="Edit.Price"
                 value=${ls.draftPrice}
                 onInput=${(/** @type {InputEvent} */ e) => { ls.draftPrice.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
        </div>
      </div>
    `}
  `;
}

// ── ReviewDrawer component ──────────────────────────────────────────────────

/**
 * The inline edit / question drawer for one line. Two modes (plantry-15l3):
 *   • "focus" — the single focused exception. Renders a question header (review-q + why),
 *     numbered alternative shortcuts, and a "Confirm & next" primary that advances the queue.
 *   • "ready" — a ready row's edit drawer. Saves via SaveLine (re-confirming the line) and
 *     offers "Wrong product — review again" (ReopenLine + refocus).
 * Save / skip / rematch are delegated to the mount-scope handlers so the keyboard shortcuts and
 * the buttons share one code path; the drawer is otherwise presentational.
 *
 * @param {{
 *   ls: LineState,
 *   products: ProductHydration[],
 *   units: UnitHydration[],
 *   locations: LocationHydration[],
 *   categories: CategoryHydration[],
 *   today: string,
 *   mode: "focus" | "ready",
 *   question: { question: string, why: string } | null,
 *   onSave: (ls: LineState, mode: "focus" | "ready") => void,
 *   onSkip: (ls: LineState) => void,
 *   onRematch: (ls: LineState) => void,
 * }} props
 */
function ReviewDrawer({ ls, products, units, locations, categories, today, mode, question, onSave, onSkip, onRematch }) {
  const listboxId = `edit-product-${ls.lineId}-listbox`;
  const isFocus = mode === "focus";

  const locationDisplay = computed(() => {
    const id = ls.draftLocationId.value;
    if (!id) return "—";
    return locations.find((l) => l.id === id)?.name ?? "—";
  });

  const priceReadOnly = !!ls.price.value;
  const primaryLabel = ls.saving.value ? "Saving…" : (isFocus ? "Confirm & next" : "Save changes");

  return html`
    <div class="import-row__edit review-row__edit">
      ${ls.error.value && html`
        <div class="import-row__error" role="alert">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
          ${ls.error.value}
        </div>
      `}
      <form onSubmit=${(/** @type {Event} */ e) => { e.preventDefault(); onSave(ls, mode); }}>
        ${isFocus && question && html`
          <div class="review-q">${question.question}</div>
          <div class="match-suggest__label">${question.why}</div>
        `}
        <${AlternativesStrip} ls=${ls} showKbd=${isFocus} />

        <div class="import-row__edit-grid">
          <${ProductSearch} ls=${ls} products=${products} listboxId=${listboxId} />

          ${ls.createNew.value && html`
            <div class="form-grid__field" style="grid-column: span 3">
              <label class="form-grid__field__label">New product name</label>
              <div class="form-grid__field__control">
                <input class="field__input" name="Edit.NewProductName"
                       placeholder=${ls.receiptText}
                       value=${ls.draftNewName}
                       onInput=${(/** @type {InputEvent} */ e) => { ls.draftNewName.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
              </div>
            </div>
            <div class="form-grid__field">
              <label class="form-grid__field__label">Category</label>
              <div class="form-grid__field__control">
                <select class="field__input" name="Edit.NewProductCategoryId"
                        value=${ls.draftNewCategoryId}
                        onChange=${(/** @type {Event} */ e) => { ls.draftNewCategoryId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
                  <option value="">— Category —</option>
                  ${categories.map((c) => html`<option key=${c.id} value=${c.id}>${c.name}</option>`)}
                </select>
              </div>
            </div>
          `}

          <div class="review-row__mode-toggle">
            ${!ls.createNew.value
              ? html`<button type="button" class="btn btn--ghost btn--sm"
                             onClick=${() => { ls.createNew.value = true; }}>
                       + Add as new product
                     </button>`
              : html`<button type="button" class="btn btn--ghost btn--sm"
                             onClick=${() => { ls.createNew.value = false; }}>
                       ← Use existing product
                     </button>`}
          </div>

          ${ls.estimate && html`
            <p class="rd-estimate-hint" style="grid-column: span 2">
              <svg class="icon" aria-hidden="true"><use href="#i-sparkle" /></svg>
              ${estimateHint(ls.estimate)}
            </p>`}

          <div class="form-grid__field" style="grid-column: span 2">
            <label class="form-grid__field__label">Quantity</label>
            <div class="form-grid__field__control">
              <div class="stepper" role="group">
                <button type="button" class="stepper__btn"
                        aria-label="Decrease quantity"
                        onClick=${() => {
                          const v = parseFloat(ls.draftQty.value) || 0;
                          ls.draftQty.value = String(Math.max(0.001, v - 1));
                        }}>
                  <svg class="icon" aria-hidden="true"><use href="#i-minus" /></svg>
                </button>
                <input class="stepper__val" type="number" step="any" min="0"
                       name="Edit.Quantity"
                       value=${ls.draftQty}
                       onInput=${(/** @type {InputEvent} */ e) => { ls.draftQty.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
                <button type="button" class="stepper__btn"
                        aria-label="Increase quantity"
                        onClick=${() => {
                          const v = parseFloat(ls.draftQty.value) || 0;
                          ls.draftQty.value = String(v + 1);
                        }}>
                  <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>
                </button>
              </div>
            </div>
          </div>

          <div class="form-grid__field">
            <label class="form-grid__field__label">Location</label>
            <div class="form-grid__field__control">
              <select class="field__input" name="Edit.LocationId"
                      value=${ls.draftLocationId}
                      onChange=${(/** @type {Event} */ e) => { ls.draftLocationId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
                <option value="">— Location —</option>
                ${locations.map((l) => html`<option key=${l.id} value=${l.id}>${l.name}</option>`)}
              </select>
            </div>
          </div>

          <${ExpiryPriceFields} ls=${ls} units=${units} today=${today} priceReadOnly=${priceReadOnly} />
        </div>

        <div class="import-row__edit-foot">
          <div class="import-row__edit-foot-summary">
            <svg class="icon" aria-hidden="true"><use href="#i-location" /></svg>
            Goes to <strong>${locationDisplay}</strong>
            ${ls.createNew.value && html`<span class="import-row__new"> · new product</span>`}
          </div>
          <span class="import-row__edit-spacer"></span>
          ${mode === "ready" && html`
            <button type="button" class="btn btn--ghost btn--sm"
                    disabled=${ls.saving.value}
                    onClick=${() => onRematch(ls)}>
              Wrong product — review again
            </button>
          `}
          <button type="button" class="btn btn--ghost btn--sm import-row__edit-danger"
                  disabled=${ls.saving.value}
                  onClick=${() => onSkip(ls)}>
            Not pantry stock — remove
          </button>
          <button type="submit" class="btn btn--primary btn--sm"
                  disabled=${ls.saving.value}>
            <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
            ${primaryLabel}
          </button>
        </div>
      </form>
    </div>
  `;
}

// ── ReviewRow component ──────────────────────────────────────────────────────

/**
 * One review row. Its section (needs / ready / skipped) is derived from lineSection(ls); the flow is
 * exceptions-first (plantry-15l3):
 *   • skipped  — a dismissed non-stock line with "Add anyway".
 *   • ready    — Confirmed / Committed, OR an auto-confirmable Pending line. No per-row Confirm button
 *                (commit-time auto-confirm handles High-confidence complete lines); a chevron opens the
 *                ready-mode edit drawer.
 *   • needs    — a genuine exception. The FOCUSED one (focusId === lineId) auto-opens its question
 *                drawer; the rest are collapsed and clicking one jumps focus to it.
 *
 * @param {{
 *   ls: LineState,
 *   products: ProductHydration[],
 *   units: UnitHydration[],
 *   locations: LocationHydration[],
 *   categories: CategoryHydration[],
 *   today: string,
 *   filter: import("@preact/signals").Signal<string>,
 *   focusId: import("@preact/signals").Signal<string|null>,
 *   onSave: (ls: LineState, mode: "focus" | "ready") => void,
 *   onSkip: (ls: LineState) => void,
 *   onRematch: (ls: LineState) => void,
 *   onRestore: (ls: LineState) => void,
 *   onJumpFocus: (lineId: string) => void,
 * }} props
 */
function ReviewRow({ ls, products, units, locations, categories, today, filter, focusId, onSave, onSkip, onRematch, onRestore, onJumpFocus }) {
  const section = computed(() => lineSection(ls));
  const status = ls.status.value;
  const sect = section.value;
  const isDismissed = sect === "skipped";
  const isCommitted = status === "Committed";
  const isFocused = focusId.value === ls.lineId;
  const unmatch = isUnmatched(ls);

  // Two symmetric latches keep a row from jumping sections mid-interaction:
  //  • the focused exception stays an exception even once its draft becomes complete-prefill (which
  //    would otherwise flip its lineSection to "ready" and close the question drawer under the user);
  //  • a ready row whose own edit drawer is open stays in the ready section even if an in-flight edit
  //    (e.g. clearing the quantity to retype it) transiently makes its prefill incomplete — otherwise
  //    the row would eject to "needs" and its drawer would unmount. Focused exceptions keep
  //    drawerOpen=false and unfocused needs rows never set it, so neither is affected by this latch.
  const asException = !isDismissed && !ls.drawerOpen.value && (sect === "needs" || isFocused);
  const isReady = !isDismissed && !asException; // ready row (incl. an open, mid-edit one) — not the focused exception

  const visible = computed(() => {
    const f = filter.value;
    if (f === "all") return true;
    if (f === "review") return asException;
    if (f === "ready") return isReady;
    return false;
  });
  if (!visible.value) return null;

  // Row state class: ready rows read confirmed/committed; exceptions read matched/unmatched by whether
  // a product is resolvable. The focused exception also carries the --open/--focus emphasis.
  const stateClass = isDismissed ? "import-row--dismissed"
    : isCommitted ? "import-row--committed"
    : isReady ? "import-row--confirmed"
    : unmatch ? "import-row--unmatched"
    : "import-row--matched";

  // Pack sizes for the drafted product — feeds the "which size?" decision variant.
  const skuCount = computed(() => {
    const pid = ls.draftProductId.value;
    return products.find((p) => p.id === pid)?.skus?.length ?? 0;
  });
  const question = computed(() => questionCopy(decisionVariant(ls, skuCount.value)));

  const displayProductName = computed(() => {
    if (ls.isNewProduct) return ls.newProductName;
    const pid = ls.draftProductId.value;
    if (pid) {
      const p = products.find((x) => x.id === pid);
      if (p) return p.name;
    }
    return ls.draftProductName.value || null;
  });

  const priceDisplay = computed(() => {
    const p = ls.price.value;
    if (p == null) return "—";
    return p.toLocaleString(undefined, { style: "currency", currency: "CAD", minimumFractionDigits: 2 });
  });
  const qtyDisplay = computed(() => {
    const q = ls.draftQty.value;
    const n = parseFloat(q);
    if (!q || isNaN(n)) return "—";
    return n.toLocaleString(undefined, { maximumFractionDigits: 3 });
  });
  const unitDisplay = computed(() => {
    const uid = ls.draftUnitId.value;
    if (!uid) return "";
    return units.find((u) => u.id === uid)?.code ?? "";
  });
  const expiryDisplay = computed(() => {
    if (ls.draftExpiryMode.value !== "has" || !ls.draftExpiry.value) return "—";
    const d = new Date(ls.draftExpiry.value + "T00:00:00");
    return d.toLocaleDateString(undefined, { day: "numeric", month: "short" });
  });

  const rowId = `import-line-${ls.lineId}`;
  // A "needs" row shows its drawer when it is the focused exception; a "ready" row shows its edit
  // drawer when its own drawerOpen toggle is set.
  const drawerVisible = isReady ? ls.drawerOpen.value : isFocused;
  const drawerMode = isReady ? "ready" : "focus";

  // Confidence badge for exception rows (canonical _ConfidenceBadge tones).
  const confidenceBadge = () => (!displayProductName.value
    ? html`<span class="badge badge-confidence--none"><svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> No match</span>`
    : html`<span class="badge badge-confidence--low"><svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> Check match</span>`);

  return html`
    <div id=${rowId}
         class=${"import-row " + stateClass
            + (drawerVisible ? " import-row--open" : "")
            + (!isReady && !isDismissed && isFocused ? " import-row--focus" : "")}
         data-status=${sect}>

      ${ls.error.value && !drawerVisible && html`
        <div class="import-row__error" role="alert">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> ${ls.error.value}
        </div>
      `}

      ${isDismissed
        ? html`
          <div class="import-row__nonstock-main">
            <div class="import-row__nonstock-ico">
              <svg class="icon" aria-hidden="true"><use href="#i-tag" /></svg>
            </div>
            <div class="import-row__nonstock-name">
              ${ls.receiptText}
              <span class="import-row__nonstock-why">
                ${priceDisplay} · not inventory — won't be added
              </span>
            </div>
            <button type="button" class="btn btn--ghost btn--sm"
                    disabled=${ls.saving.value}
                    onClick=${() => onRestore(ls)}>
              Add anyway
            </button>
          </div>
        `
        : isReady
        ? html`
          <div class="import-row__main"
               data-action=${isCommitted ? undefined : "toggle-edit"}
               onClick=${isCommitted ? undefined : () => { ls.drawerOpen.value = !ls.drawerOpen.value; }}>
            <div class="import-row__id">
              <div class="import-row__name">
                ${displayProductName.value
                  ? html`
                    <span class="import-row__product">${displayProductName.value}</span>
                    ${ls.isNewProduct && html`<span class="import-row__new"> · new product</span>`}
                  `
                  : html`<span class="import-row__unrecognized">Unrecognized item</span>`
                }
              </div>
              <div class="import-row__raw">
                <span class="import-row__raw-tag">receipt</span>
                <span>${ls.receiptText}</span>
              </div>
            </div>

            <div class="import-row__meta">
              <div class="import-row__meta-cell">
                <div class="import-row__meta-value">
                  ${qtyDisplay}<span class="import-row__meta-unit"> ${unitDisplay}</span>
                </div>
                <div class="import-row__meta-label">qty</div>
              </div>
              <div class="import-row__meta-cell">
                <div class="import-row__meta-value">${priceDisplay}</div>
                <div class="import-row__meta-label">price</div>
              </div>
              <div class="import-row__meta-cell">
                <div class="import-row__meta-value">${expiryDisplay}</div>
                <div class="import-row__meta-label">expires</div>
              </div>
            </div>

            <div class="import-row__act">
              ${isCommitted
                ? html`
                  <span class="import-row__locked">
                    <svg class="icon" aria-hidden="true"><use href="#i-lock" /></svg> Added
                  </span>`
                : html`
                  <span class="import-row__confirmed-flag">
                    <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> Ready
                  </span>
                  <button type="button" class="import-row__toggle"
                          onClick=${(/** @type {Event} */ e) => { e.stopPropagation(); ls.drawerOpen.value = !ls.drawerOpen.value; }}
                          aria-expanded=${String(ls.drawerOpen.value)}
                          aria-label="Edit line">
                    <svg class=${"icon import-row__chev" + (ls.drawerOpen.value ? " import-row__chev--open" : "")}
                         aria-hidden="true">
                      <use href="#i-chevron" />
                    </svg>
                  </button>`
              }
            </div>
          </div>
        `
        : html`
          <div class="import-row__main"
               data-action=${isFocused ? undefined : "jump"}
               onClick=${isFocused ? undefined : () => onJumpFocus(ls.lineId)}>
            <div class="import-row__id">
              <div class="import-row__name">
                ${displayProductName.value
                  ? html`<span class="import-row__product">${displayProductName.value}</span>`
                  : html`<span class="import-row__unrecognized">Unrecognized item</span>`
                }
              </div>
              <div class="import-row__raw">
                <span class="import-row__raw-tag">receipt</span>
                <span>${ls.receiptText}</span>
              </div>
            </div>
            <div class="import-row__conf">${confidenceBadge()}</div>
            <div class="import-row__act">
              <button type="button" class="import-row__toggle"
                      onClick=${(/** @type {Event} */ e) => { e.stopPropagation(); onJumpFocus(ls.lineId); }}
                      aria-expanded=${String(isFocused)}
                      aria-label="Review line">
                <svg class=${"icon import-row__chev" + (isFocused ? " import-row__chev--open" : "")}
                     aria-hidden="true">
                  <use href="#i-chevron" />
                </svg>
              </button>
            </div>
          </div>
        `
      }

      ${drawerVisible && !isDismissed && !isCommitted && html`
        <${ReviewDrawer}
          ls=${ls}
          products=${products}
          units=${units}
          locations=${locations}
          categories=${categories}
          today=${today}
          mode=${drawerMode}
          question=${drawerMode === "focus" ? question.value : null}
          onSave=${onSave}
          onSkip=${onSkip}
          onRematch=${onRematch} />
      `}
    </div>
  `;
}

// ── App component ────────────────────────────────────────────────────────────

/**
 * @param {{
 *   lines: import("@preact/signals").Signal<LineState[]>,
 *   products: ProductHydration[],
 *   units: UnitHydration[],
 *   locations: LocationHydration[],
 *   categories: CategoryHydration[],
 *   session: SessionHydration,
 *   filter: import("@preact/signals").Signal<string>,
 *   focusId: import("@preact/signals").Signal<string|null>,
 *   alert: import("@preact/signals").Signal<string>,
 *   toastMsg: import("@preact/signals").Signal<string>,
 *   toastUndo: import("@preact/signals").Signal<boolean>,
 *   onSave: (ls: LineState, mode: "focus" | "ready") => void,
 *   onSkip: (ls: LineState) => void,
 *   onRematch: (ls: LineState) => void,
 *   onRestore: (ls: LineState) => void,
 *   onJumpFocus: (lineId: string) => void,
 *   onUndo: () => void,
 *   onDismissToast: () => void,
 *   onCommit: () => void,
 *   onDiscard: () => void,
 * }} props
 */
function App({ lines, products, units, locations, categories, session, filter, focusId, alert, toastMsg, toastUndo, onSave, onSkip, onRematch, onRestore, onJumpFocus, onUndo, onDismissToast, onCommit, onDiscard }) {
  const allLines = lines.value;

  // Commit-bar arithmetic — one pure source of truth (commitBarCounts in logic.js), so the
  // displayed "to resolve" count and the commit gate can't diverge. Per-value computeds keep the
  // existing call sites unchanged.
  const bar = computed(() => commitBarCounts(lines.value.map(lineSection)));
  const needsCount = computed(() => bar.value.needsCount);
  const readyCount = computed(() => bar.value.readyCount);
  const totalItems = computed(() => bar.value.totalItems);
  const canCommit = computed(() => bar.value.canCommit);
  const progressPct = computed(() => bar.value.progressPct);
  const remaining = computed(() => bar.value.remaining);

  // "Adding N items · $val" — everything that will land in the pantry on commit is the ready section
  // (Confirmed / Committed + the High-confidence Pending lines the server auto-confirms), so the
  // summary is section-driven, matching the commit gate.
  const readyLines = computed(() => lines.value.filter((l) => lineSection(l) === "ready"));
  const addingCount = computed(() => readyLines.value.length);
  const addingValue = computed(() => readyLines.value.reduce((sum, l) => sum + (l.price.value ?? 0), 0));

  // Receipt total: non-dismissed lines
  const receiptTotal = computed(() =>
    lines.value
      .filter((l) => l.status.value !== "Dismissed")
      .reduce((sum, l) => sum + (l.price.value ?? 0), 0)
  );

  const fmtCad = (/** @type {number} */ n) =>
    n.toLocaleString(undefined, { style: "currency", currency: "CAD", minimumFractionDigits: 2 });

  const needsMeta = computed(() => {
    if (canCommit.value) return `<span><b>All set.</b> ${readyCount.value} items ready to add.</span>`;
    return `<span><b>${needsCount.value}</b> ${needsCount.value === 1 ? "decision" : "decisions"} left · <b>${readyCount.value}</b> ready</span>`;
  });

  // DISPLAY partition (vs the commit-truth partition above), with the same two latches ReviewRow
  // applies so a row never relocates lists mid-interaction: the focused line stays in the exceptions
  // area until resolved, and a ready row with its edit drawer open stays in the ready section even if
  // an in-flight edit transiently makes its prefill incomplete.
  const sectionRows = computed(() => {
    const fid = focusId.value;
    /** @param {LineState} l @returns {"needs"|"ready"|"skipped"} */
    const displaySect = (l) => {
      const s = lineSection(l);
      if (s === "skipped") return "skipped";
      if (l.lineId === fid) return "needs";        // focused-exception latch
      if (l.drawerOpen.value) return "ready";      // ready-row edit-in-progress latch
      return s;
    };
    const rows = lines.value;
    return {
      needs: rows.filter((l) => displaySect(l) === "needs"),
      ready: rows.filter((l) => displaySect(l) === "ready"),
      skipped: rows.filter((l) => displaySect(l) === "skipped"),
    };
  });

  const showNeeds = filter.value === "all" || filter.value === "review";
  const showReady = filter.value === "all" || filter.value === "ready";
  const showSkipped = filter.value === "all";

  return html`
    <div class="review">
      <aside class="review__receipt rcpt-pane">
        <div class="rcpt-meta-top">
          <span class="rcpt-via">
            <svg class="icon" aria-hidden="true"><use href=${session.scanVia === "email" ? "#i-receipt" : "#i-camera"} /></svg>
            ${session.scanVia === "email" ? "Forwarded by email" : "Receipt photo"}
          </span>
          <span style="margin-left:auto">${session.scannedLabel}</span>
        </div>
        <div class="receipt">
          <div class="rcpt-store">
            <div class="rcpt-store-name">${session.merchantText || "Receipt"}</div>
            ${(session.storeBranch || session.purchaseDate || session.purchaseTime) && html`
              <div class="rcpt-store-sub">
                ${session.storeBranch && html`<div>${session.storeBranch}</div>`}
                ${(session.purchaseDate || session.purchaseTime) && html`
                  <div>${[session.purchaseDate, session.purchaseTime].filter(Boolean).join(" · ")}</div>
                `}
              </div>
            `}
          </div>
          <hr class="rcpt-rule" />
          ${allLines.map((ls) => html`
            <div key=${ls.lineId} class=${"rcpt-line" + (ls.status.value === "Dismissed" ? " dim" : "")}>
              <span class="rl-name">${ls.receiptText}</span>
              <span class="rl-price">${ls.price.value != null ? ls.price.value.toFixed(2) : ""}</span>
            </div>
          `)}
          <hr class="rcpt-rule" />
          <div class="rcpt-foot">
            ${session.subtotal != null && html`
              <div class="rcpt-line">
                <span class="rl-name">SUBTOTAL</span>
                <span class="rl-price">${session.subtotal.toFixed(2)}</span>
              </div>
            `}
            ${session.tax != null && html`
              <div class="rcpt-line">
                <span class="rl-name">TAX</span>
                <span class="rl-price">${session.tax.toFixed(2)}</span>
              </div>
            `}
            <div id="rcpt-total" class="rcpt-line rcpt-total">
              <span class="rl-name">TOTAL</span>
              <span class="rl-price">${(session.total != null ? session.total : receiptTotal.value).toFixed(2)}</span>
            </div>
            ${session.payment && html`
              <div class="rcpt-line" style="margin-top:6px">
                <span class="rl-name">${session.payment}</span>
              </div>
            `}
          </div>
          <div class="rcpt-barcode"></div>
          ${session.receiptNo && html`<div class="rcpt-no">${session.receiptNo}</div>`}
        </div>
      </aside>

      <section class="review__lines">
        <div id="review-alert" aria-live="polite">
          ${alert.value && html`<div class="review-alert review-alert--error">${alert.value}</div>`}
        </div>

        <div class="rev-head">
          <div class="rev-title-row">
            <div class="rev-title">
              <h2>Review import</h2>
              <p>${session.merchantText || "Receipt"} · ${session.sessionDate} · ${totalItems} items to add</p>
            </div>
            <span class="rev-title-row__spacer"></span>
            <div id="rev-chips" class="filter-chip-bar">
              <button type="button" class=${"filter-chip" + (filter.value === "all" ? " is-active filter-chip--success" : "")}
                      onClick=${() => { filter.value = "all"; }}>
                <span class="chip-inner">
                  <span class="chip-dot" style="background: var(--color-success)"></span>
                  All <span class="chip-count">${totalItems}</span>
                </span>
              </button>
              <button type="button" class=${"filter-chip" + (filter.value === "review" ? " is-active filter-chip--warning" : "")}
                      onClick=${() => { filter.value = "review"; }}>
                <span class="chip-inner">
                  <span class="chip-dot" style="background: var(--color-warning)"></span>
                  Needs review <span class="chip-count">${needsCount}</span>
                </span>
              </button>
              <button type="button" class=${"filter-chip" + (filter.value === "ready" ? " is-active filter-chip--success" : "")}
                      onClick=${() => { filter.value = "ready"; }}>
                <span class="chip-inner">
                  <span class="chip-dot" style="background: var(--color-success)"></span>
                  Ready <span class="chip-count">${readyCount}</span>
                </span>
              </button>
            </div>
          </div>
          <div id="rev-progress">
            <div class="meter meter--intake-progress meter--meta-line">
              <div class="meter__track">
                <div class="meter__fill meter__fill--accent" style=${`width: ${progressPct}%`}></div>
              </div>
              <div class="meter__meta" dangerouslySetInnerHTML=${{ __html: needsMeta.value }}></div>
            </div>
          </div>
        </div>

        ${allLines.length === 0
          ? html`<p class="review__empty">No line items were found on this receipt.</p>`
          : html`
            <div class="rev-list">
              ${showNeeds && sectionRows.value.needs.length > 0 && html`
                <div class="sec-label">
                  Needs your review <span class="sec-label__count">· ${sectionRows.value.needs.length}</span>
                </div>
              `}
              ${sectionRows.value.needs.filter(() => showNeeds).map((ls) => html`
                <${ReviewRow} key=${ls.lineId} ls=${ls}
                  products=${products} units=${units} locations=${locations} categories=${categories}
                  today=${session.today}
                  filter=${filter} focusId=${focusId}
                  onSave=${onSave} onSkip=${onSkip} onRematch=${onRematch}
                  onRestore=${onRestore} onJumpFocus=${onJumpFocus} />
              `)}
              ${showNeeds && focusId.value && sectionRows.value.needs.length > 0 && html`
                <div class="kbd-bar kbd-bar--inline">
                  <span><kbd>1</kbd>–<kbd>9</kbd> choose</span>
                  <span><kbd>Enter</kbd> confirm &amp; next</span>
                  <span><kbd>N</kbd> new product</span>
                  <span><kbd>S</kbd> skip</span>
                  <span><kbd>U</kbd> undo</span>
                </div>
              `}
              ${showNeeds && sectionRows.value.needs.length === 0 && filter.value !== "ready" && html`
                <div class="sec-label">
                  Needs your review <span class="sec-label__count">· 0</span>
                </div>
                <p class="review__empty" style="padding: var(--space-3)">
                  <b>All caught up.</b> Everything below goes to your pantry when you commit.
                </p>
              `}

              ${showReady && sectionRows.value.ready.length > 0 && html`
                <div class="sec-label">
                  Ready to add <span class="sec-label__count">· ${sectionRows.value.ready.length} · ${fmtCad(sectionRows.value.ready.reduce((s, l) => s + (l.price.value ?? 0), 0))}</span>
                </div>
              `}
              ${sectionRows.value.ready.filter(() => showReady).map((ls) => html`
                <${ReviewRow} key=${ls.lineId} ls=${ls}
                  products=${products} units=${units} locations=${locations} categories=${categories}
                  today=${session.today}
                  filter=${filter} focusId=${focusId}
                  onSave=${onSave} onSkip=${onSkip} onRematch=${onRematch}
                  onRestore=${onRestore} onJumpFocus=${onJumpFocus} />
              `)}

              ${showSkipped && sectionRows.value.skipped.length > 0 && html`
                <div class="sec-label">
                  Skipped — not inventory <span class="sec-label__count">· ${sectionRows.value.skipped.length}</span>
                </div>
              `}
              ${sectionRows.value.skipped.filter(() => showSkipped).map((ls) => html`
                <${ReviewRow} key=${ls.lineId} ls=${ls}
                  products=${products} units=${units} locations=${locations} categories=${categories}
                  today=${session.today}
                  filter=${filter} focusId=${focusId}
                  onSave=${onSave} onSkip=${onSkip} onRematch=${onRematch}
                  onRestore=${onRestore} onJumpFocus=${onJumpFocus} />
              `)}
            </div>
          `
        }

        <div id="commit-bar" class="review__commit">
          <div class="commit-bar">
            <button type="button" class="btn btn--ghost commit-bar__cancel"
                    onClick=${onDiscard}>
              Cancel
            </button>
            <span class="commit-bar__spacer"></span>
            ${remaining.value > 0 && html`
              <span class="commit-bar__warn">
                <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
                ${remaining.value} to resolve
              </span>
            `}
            <div class="commit-bar__summary">
              Adding <b>${addingCount}</b> items ·
              <b class="mono">${fmtCad(addingValue.value)}</b>
            </div>
            <button type="button" class="btn btn--primary"
                    disabled=${!canCommit.value}
                    onClick=${onCommit}>
              <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
              Add to pantry
            </button>
          </div>
        </div>
      </section>

      ${toastMsg.value && html`
        <div class="toast" role="status" aria-live="polite"
             onClick=${(/** @type {MouseEvent} */ e) => {
               if (!(/** @type {HTMLElement} */ (e.target).closest("[data-action]"))) onDismissToast();
             }}>
          <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
          <span>${toastMsg.value}</span>
          ${toastUndo.value && html`
            <button type="button" class="toast__action" data-action="undo" onClick=${onUndo}>Undo</button>
          `}
        </div>
      `}
    </div>
  `;
}

// ── Mount ───────────────────────────────────────────────────────────────────

/**
 * @param {Element} root
 * @param {SessionHydration} hydration
 */
export function mountIntakeReview(root, hydration) {
  const token = readAntiforgeryToken();
  const linesSignal = signal(hydration.lines.map(makeLine));
  const filter = signal("all");
  const alertMsg = signal("");

  // Exceptions-first flow state (plantry-15l3): the single focused exception, and the undo toast.
  const focusId = signal(/** @type {string|null} */ (firstNeedsLineId(linesSignal.value)));
  const toastMsg = signal("");
  const toastUndo = signal(false);
  /** @type {{ fn: (() => void | Promise<void>) | null }} */
  const undoRef = { fn: null };
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let toastTimer;

  // ── Focus / toast primitives ─────────────────────────────────────────────────

  /** Advance the queue to the next unresolved exception (or clear focus when none remain). */
  function advanceFocus() {
    focusId.value = firstNeedsLineId(linesSignal.value);
  }

  /** @param {string} lineId */
  function focusLine(lineId) {
    focusId.value = lineId;
    // Scroll the newly-focused row into view after Preact commits the open drawer.
    setTimeout(() => {
      document.getElementById(`import-line-${lineId}`)?.scrollIntoView({ behavior: "smooth", block: "center" });
    }, 0);
  }

  /** @param {string} msg @param {(() => void | Promise<void>) | null} undoFn */
  function showToast(msg, undoFn) {
    undoRef.fn = undoFn ?? null;
    batch(() => { toastMsg.value = msg; toastUndo.value = !!undoFn; });
    clearTimeout(toastTimer);
    toastTimer = setTimeout(hideToast, 6000);
  }

  function hideToast() {
    undoRef.fn = null;
    batch(() => { toastMsg.value = ""; toastUndo.value = false; });
    clearTimeout(toastTimer);
  }

  async function doUndo() {
    const fn = undoRef.fn;
    hideToast();
    if (fn) await fn();
  }

  /** @param {LineState} ls @returns {string} */
  function displayName(ls) {
    if (ls.isNewProduct && ls.newProductName) return ls.newProductName;
    const pid = ls.draftProductId.value;
    if (pid) {
      const p = hydration.products.find((x) => x.id === pid);
      if (p) return p.name;
    }
    return ls.draftProductName.value || ls.receiptText;
  }

  // ── Server state application (server is authoritative — ADR-020 §2/§7) ─────────

  /** @param {LineState} ls @param {object} data */
  function baseOnSaved(ls, data) {
    batch(() => {
      ls.status.value = data.status ?? ls.status.value;
      ls.isNewProduct = data.isNewProduct ?? ls.isNewProduct;
      ls.newProductName = data.newProductName ?? ls.newProductName;
      if (typeof data.price === "number") ls.price.value = data.price;
      if (data.productId) ls.draftProductId.value = data.productId;
      if (data.productName) ls.draftProductName.value = data.productName;
      ls.drawerOpen.value = false;
      ls.error.value = null;
    });
  }

  /**
   * POSTs a lineId-scoped status action (Dismiss/Restore/Reopen) and returns the JSON body on
   * success or false on failure (setting ls.error). Shared by skip/restore/rematch and their undos.
   * @param {string} url @param {LineState} ls @returns {Promise<any|false>}
   */
  async function postAction(url, ls) {
    if (ls.saving.value) return false;
    ls.saving.value = true;
    ls.error.value = null;
    try {
      const resp = await postJson(`${url}&lineId=${ls.lineId}`, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) {
        ls.error.value = data.error ?? `Action failed (${resp.status})`;
        return false;
      }
      return data;
    } catch {
      ls.error.value = "Network error — please try again.";
      return false;
    } finally {
      ls.saving.value = false;
    }
  }

  /** @param {LineState} ls @returns {Promise<boolean>} */
  async function postDismiss(ls) {
    const data = await postAction(hydration.dismissLineUrl, ls);
    if (!data) return false;
    batch(() => { ls.status.value = "Dismissed"; ls.drawerOpen.value = false; ls.error.value = null; });
    return true;
  }

  /** @param {LineState} ls @returns {Promise<boolean>} */
  async function postRestore(ls) {
    const data = await postAction(hydration.restoreLineUrl, ls);
    if (!data) return false;
    batch(() => { ls.status.value = data.status ?? "Pending"; ls.error.value = null; });
    return true;
  }

  /** @param {LineState} ls @returns {Promise<boolean>} */
  async function postReopen(ls) {
    const data = await postAction(hydration.reopenLineUrl, ls);
    if (!data) return false;
    batch(() => { ls.status.value = data.status ?? "Pending"; ls.error.value = null; });
    return true;
  }

  // ── Client-side validation mirror (server re-validates and is authoritative) ────

  /** @param {LineState} ls @returns {string|null} */
  function validateLine(ls) {
    const qty = parseFloat(ls.draftQty.value);
    if (ls.createNew.value) {
      if (!ls.draftNewName.value.trim() || !ls.draftNewCategoryId.value)
        return "A new product needs a name and a category.";
    } else if (!ls.draftProductId.value) {
      return "Choose a product, or switch to creating a new one.";
    }
    if (!qty || qty <= 0) return "Enter a quantity greater than zero.";
    if (!ls.draftUnitId.value) return "Choose a unit.";
    if (!ls.draftLocationId.value) return "Choose a location.";
    return null;
  }

  // ── Queue actions ──────────────────────────────────────────────────────────────

  /**
   * Resolve a line via SaveLine (confirming it). In "focus" mode this is "Confirm & next": it advances
   * the queue and offers an Undo that reopens the line. In "ready" mode it is a ready-row edit save.
   * @param {LineState} ls @param {"focus" | "ready"} mode
   */
  async function saveLine(ls, mode) {
    if (ls.saving.value) return;
    const err = validateLine(ls);
    if (err) { ls.error.value = err; return; }
    ls.saving.value = true;
    ls.error.value = null;
    await submitSaveLine(ls, buildSaveLineBody(ls), hydration.saveLineUrl, token, (l, data) => {
      baseOnSaved(l, data);
      const name = displayName(l);
      if (mode === "focus") {
        showToast(`${name} confirmed`, () => undoResolve(l));
        advanceFocus();
      } else {
        showToast(`${name} updated`, null);
      }
    });
  }

  /** Skip / remove a line as not-pantry-stock (DismissLine), with an Undo that restores it. @param {LineState} ls */
  async function skipLine(ls) {
    if (ls.saving.value) return;
    const name = displayName(ls);
    const wasFocused = focusId.value === ls.lineId;
    if (await postDismiss(ls)) {
      showToast(`${name} skipped`, () => undoSkip(ls));
      if (wasFocused) advanceFocus();
    }
  }

  /** "Add anyway" on a skipped row (RestoreLine), with an Undo that dismisses it again. @param {LineState} ls */
  async function restoreLine(ls) {
    if (ls.saving.value) return;
    const name = displayName(ls);
    if (await postRestore(ls)) {
      showToast(`${name} added back`, () => undoRestore(ls));
      if (lineSection(ls) === "needs") focusLine(ls.lineId);
    }
  }

  /**
   * "Wrong product — review again" on a ready row: reopen a Confirmed line to Pending, clear the product
   * match so it re-enters the exceptions queue, and focus it. @param {LineState} ls
   */
  async function rematchLine(ls) {
    if (ls.saving.value) return;
    if (ls.status.value === "Confirmed") {
      if (!(await postReopen(ls))) return;
    }
    batch(() => {
      ls.draftProductId.value = "";
      ls.draftSkuId.value = "";
      ls.createNew.value = false;
      ls.drawerOpen.value = false;
    });
    focusLine(ls.lineId);
  }

  /** @param {string} lineId */
  function jumpFocus(lineId) {
    focusLine(lineId);
  }

  // ── Undo closures (each maps to the inverse server endpoint) ────────────────────

  /** @param {LineState} ls */
  async function undoResolve(ls) { if (await postReopen(ls)) focusLine(ls.lineId); }
  /** @param {LineState} ls */
  async function undoSkip(ls) { if (await postRestore(ls) && lineSection(ls) === "needs") focusLine(ls.lineId); }
  /** @param {LineState} ls */
  async function undoRestore(ls) { await postDismiss(ls); }

  // ── Commit / discard ─────────────────────────────────────────────────────────

  async function onCommit() {
    try {
      const resp = await postJson(hydration.commitUrl, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) {
        alertMsg.value = data.error ?? `Commit failed (${resp.status})`;
        return;
      }
      if (data.redirectUrl) window.location.href = data.redirectUrl;
    } catch {
      alertMsg.value = "Network error — please try again.";
    }
  }

  async function onDiscard() {
    try {
      const resp = await postJson(hydration.discardUrl, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) {
        alertMsg.value = data.error ?? `Discard failed (${resp.status})`;
        return;
      }
      if (data.redirectUrl) window.location.href = data.redirectUrl;
    } catch {
      alertMsg.value = "Network error — please try again.";
    }
  }

  // ── Keyboard shortcuts (exceptions-first flow) ──────────────────────────────────
  //
  // 1–9 pick an alternative · Enter confirm-and-next · N new product · S skip · U undo.
  // Shortcuts other than Undo require a focused exception and do not fire while typing in a field
  // (except Enter inside the focused form, which confirms — but never inside the product combobox,
  // which owns Enter for its own listbox).
  document.addEventListener("keydown", (e) => {
    const target = /** @type {HTMLElement} */ (e.target);
    const tag = target?.tagName;
    const inField = tag === "INPUT" || tag === "SELECT" || tag === "TEXTAREA";

    if (!inField && (e.key === "u" || e.key === "U")) { e.preventDefault(); doUndo(); return; }

    const fid = focusId.value;
    const ls = fid ? linesSignal.value.find((l) => l.lineId === fid) : null;
    if (!ls) return;

    if (e.key === "Enter") {
      // A field inside a NON-focused row (e.g. a ready row's own edit drawer, which opens on its own
      // drawerOpen signal independent of focus) owns its Enter → that form's native submit; the product
      // combobox owns Enter for its listbox. Only a bare Enter, or one inside the focused exception's own
      // row, drives confirm-and-next. (Suppressing the default here for a non-focused field would kill
      // that row's submit AND mis-action the focused line.)
      if (inField && (target.getAttribute("role") === "combobox" || !target.closest(`#import-line-${fid}`))) return;
      e.preventDefault();
      saveLine(ls, "focus");
      return;
    }
    if (inField) return; // remaining shortcuts don't fire mid-typing

    if (/^[1-9]$/.test(e.key) && !ls.createNew.value) { e.preventDefault(); pickAlternative(ls, Number(e.key) - 1); }
    else if (e.key === "n" || e.key === "N") { e.preventDefault(); ls.createNew.value = true; }
    else if (e.key === "s" || e.key === "S") { e.preventDefault(); skipLine(ls); }
  });

  // E2E / test seam
  window.__intakeReviewIsland = {
    /** @returns {number} */
    needsCount() {
      return linesSignal.value.filter((l) => lineSection(l) === "needs").length;
    },
    /** @returns {number} */
    readyCount() {
      return linesSignal.value.filter((l) => lineSection(l) === "ready").length;
    },
    /** @returns {string|null} */
    focusId() { return focusId.value; },
    /** @param {string} lineId — focus a needs exception, or open a ready row's edit drawer. */
    openDrawer(lineId) {
      const ls = linesSignal.value.find((l) => l.lineId === lineId);
      if (!ls) return;
      if (lineSection(ls) === "needs") focusLine(lineId);
      else ls.drawerOpen.value = true;
    },
  };

  render(
    html`<${App}
      lines=${linesSignal}
      products=${hydration.products}
      units=${hydration.units}
      locations=${hydration.locations}
      categories=${hydration.categories}
      session=${hydration}
      filter=${filter}
      focusId=${focusId}
      alert=${alertMsg}
      toastMsg=${toastMsg}
      toastUndo=${toastUndo}
      onSave=${saveLine}
      onSkip=${skipLine}
      onRematch=${rematchLine}
      onRestore=${restoreLine}
      onJumpFocus=${jumpFocus}
      onUndo=${doUndo}
      onDismissToast=${hideToast}
      onCommit=${onCommit}
      onDiscard=${onDiscard} />`,
    root,
  );
}
