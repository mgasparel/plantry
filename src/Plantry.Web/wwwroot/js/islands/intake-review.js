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

import { render, html, signal, computed, batch } from "./runtime.js";
import { readHydration, readAntiforgeryToken, postJson } from "./helpers.js";
import { makeLine as makeLineFromSeed, lineSection, isUnmatched, buildSaveLineBody } from "./intake-review-logic.js";

// ── Type documentation ───────────────────────────────────────────────────────

/**
 * @typedef {Object} PrefillData
 * @property {string|null} productId
 * @property {string|null} productName
 * @property {number|null} quantity
 * @property {string|null} unitId
 * @property {string|null} locationId
 * @property {string|null} locationName
 * @property {number|null} price
 * @property {string|null} expiry  ISO date string yyyy-MM-dd or null
 * @property {string|null} skuId
 */

/**
 * @typedef {Object} LineSeed
 * @property {string} lineId
 * @property {number} lineNo
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
 * @property {string} defaultUnitCode
 * @property {string} defaultUnitId
 * @property {string|null} defaultLocationId
 * @property {SkuOption[]} skus
 * @property {ProductDefaults} defaults
 * @property {string|null} categoryId
 * @property {number|null} categoryHue
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
 * @typedef {Object} SessionHydration
 * @property {string} sessionId
 * @property {string} merchantText
 * @property {string} sessionDate
 * @property {string} today        ISO date string yyyy-MM-dd
 * @property {string} commitUrl
 * @property {string} discardUrl
 * @property {string} saveLineUrl
 * @property {string} dismissLineUrl
 * @property {string} restoreLineUrl
 * @property {ProductHydration[]} products
 * @property {UnitHydration[]} units
 * @property {LocationHydration[]} locations
 * @property {CategoryHydration[]} categories
 * @property {Array<{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null}>} lines
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
 */

// ── Line factory ─────────────────────────────────────────────────────────────
//
// makeLine, lineSection, isUnmatched, and buildSaveLineBody are pure transforms
// that live in intake-review-logic.js (imported above) so they can be unit-tested
// with `node --test` without browser globals. The island calls them by passing
// the real `signal` factory from runtime.js.

/**
 * @param {{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null}} seed
 * @returns {LineState}
 */
function makeLine(seed) {
  return makeLineFromSeed(seed, signal);
}

// ── ProductSearch component ──────────────────────────────────────────────────

/**
/**
 * Posts a save-line body and applies the result to the line's signals. Shared by the drawer
 * Save and the row quick-confirm so there is ONE submit path: on failure it sets the error and
 * reopens the drawer; on success it clears the error and calls onSaved; it always clears
 * `saving` in finally. (Callers own validation + body construction via buildSaveLineBody.)
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

// ── ReviewDrawer component ──────────────────────────────────────────────────

/**
 * @param {{
 *   ls: LineState,
 *   products: ProductHydration[],
 *   units: UnitHydration[],
 *   locations: LocationHydration[],
 *   categories: CategoryHydration[],
 *   token: string,
 *   saveLineUrl: string,
 *   dismissLineUrl: string,
 *   sessionId: string,
 *   today: string,
 *   onSaved: (ls: LineState, data: object) => void,
 *   onDismissed: (ls: LineState) => void,
 * }} props
 */
function ReviewDrawer({ ls, products, units, locations, categories, token, saveLineUrl, dismissLineUrl, sessionId, today, onSaved, onDismissed }) {
  const listboxId = `edit-product-${ls.lineId}-listbox`;

  const locationDisplay = computed(() => {
    const id = ls.draftLocationId.value;
    if (!id) return "—";
    return locations.find((l) => l.id === id)?.name ?? "—";
  });

  /** @param {Event} e */
  async function handleSave(e) {
    e.preventDefault();
    if (ls.saving.value) return;

    // Client-side validation mirror (Boundary judgment call 3 — server is authoritative)
    const qty = parseFloat(ls.draftQty.value);
    if (ls.createNew.value) {
      if (!ls.draftNewName.value.trim() || !ls.draftNewCategoryId.value) {
        ls.error.value = "A new product needs a name and a category.";
        return;
      }
    } else {
      if (!ls.draftProductId.value) {
        ls.error.value = "Choose a product, or switch to creating a new one.";
        return;
      }
    }
    if (!qty || qty <= 0) {
      ls.error.value = "Enter a quantity greater than zero.";
      return;
    }
    if (!ls.draftUnitId.value) {
      ls.error.value = "Choose a unit.";
      return;
    }
    if (!ls.draftLocationId.value) {
      ls.error.value = "Choose a location.";
      return;
    }

    ls.saving.value = true;
    ls.error.value = null;
    await submitSaveLine(ls, buildSaveLineBody(ls), saveLineUrl, token, onSaved);
  }

  async function handleDismiss() {
    if (ls.saving.value) return;
    ls.saving.value = true;
    ls.error.value = null;
    try {
      const resp = await postJson(`${dismissLineUrl}&lineId=${ls.lineId}`, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) {
        ls.error.value = data.error ?? `Dismiss failed (${resp.status})`;
      } else {
        onDismissed(ls);
      }
    } catch {
      ls.error.value = "Network error — please try again.";
    } finally {
      ls.saving.value = false;
    }
  }

  const priceReadOnly = !!ls.price.value;

  return html`
    <div class="import-row__edit review-row__edit">
      ${ls.error.value && html`
        <div class="import-row__error" role="alert">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg>
          ${ls.error.value}
        </div>
      `}
      <form onSubmit=${handleSave}>
        ${/* Did you mean strip */ ls.alternatives && !ls.createNew.value && html`
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
                          onClick=${() => {
                            ls.draftProductId.value = alt.productId;
                            ls.draftProductName.value = alt.productName;
                            ls.draftSkuId.value = "";
                          }}>
                    ${selected && html`<svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>`}
                    ${alt.productName}
                    <span class="rk">${rankLabel}</span>
                  </button>
                `;
              })}
            </div>
          </div>
        `}

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
        </div>

        <div class="import-row__edit-foot">
          <div class="import-row__edit-foot-summary">
            <svg class="icon" aria-hidden="true"><use href="#i-location" /></svg>
            Goes to <strong>${locationDisplay}</strong>
            ${ls.createNew.value && html`<span class="import-row__new"> · new product</span>`}
          </div>
          <span class="import-row__edit-spacer"></span>
          <button type="button" class="btn btn--ghost btn--sm import-row__edit-danger"
                  disabled=${ls.saving.value}
                  onClick=${handleDismiss}>
            Not pantry stock — remove
          </button>
          <button type="submit" class="btn btn--primary btn--sm"
                  disabled=${ls.saving.value}>
            <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
            ${ls.saving.value ? "Saving…" : "Confirm item"}
          </button>
        </div>
      </form>
    </div>
  `;
}

// ── ReviewRow component ──────────────────────────────────────────────────────

/**
 * @param {{
 *   ls: LineState,
 *   products: ProductHydration[],
 *   units: UnitHydration[],
 *   locations: LocationHydration[],
 *   categories: CategoryHydration[],
 *   token: string,
 *   saveLineUrl: string,
 *   dismissLineUrl: string,
 *   restoreLineUrl: string,
 *   sessionId: string,
 *   today: string,
 *   filter: import("@preact/signals").Signal<string>,
 *   onSaved: (ls: LineState, data: object) => void,
 *   onDismissed: (ls: LineState) => void,
 *   onRestored: (ls: LineState) => void,
 * }} props
 */
function ReviewRow({ ls, products, units, locations, categories, token, saveLineUrl, dismissLineUrl, restoreLineUrl, sessionId, today, filter, onSaved, onDismissed, onRestored }) {
  const section = computed(() => lineSection(ls));
  const visible = computed(() => {
    const f = filter.value;
    const s = section.value;
    if (f === "all") return true;
    if (f === "review") return s === "needs";
    if (f === "ready") return s === "ready";
    return false;
  });

  if (!visible.value) return null;

  const status = ls.status.value;
  const isDismissed = status === "Dismissed";
  const isConfirmed = status === "Confirmed";
  const isCommitted = status === "Committed";
  const isMatched = status === "Pending" && ls.confidence === "High";
  const unmatch = isUnmatched(ls);

  const stateClass = isDismissed ? "import-row--dismissed"
    : isConfirmed ? "import-row--confirmed"
    : isCommitted ? "import-row--committed"
    : isMatched ? "import-row--matched"
    : "import-row--unmatched";

  // Resolve display product name
  const displayProductName = computed(() => {
    if (ls.isNewProduct) return ls.newProductName;
    const pid = ls.draftProductId.value;
    if (pid) {
      const p = products.find((x) => x.id === pid);
      if (p) return p.name;
    }
    return ls.draftProductName.value || null;
  });

  // Price display
  const priceDisplay = computed(() => {
    const p = ls.price.value;
    if (p == null) return "—";
    return p.toLocaleString(undefined, { style: "currency", currency: "CAD", minimumFractionDigits: 2 });
  });

  // Qty display
  const qtyDisplay = computed(() => {
    const q = ls.draftQty.value;
    const n = parseFloat(q);
    if (!q || isNaN(n)) return "—";
    return n.toLocaleString(undefined, { maximumFractionDigits: 3 });
  });

  // Unit display
  const unitDisplay = computed(() => {
    const uid = ls.draftUnitId.value;
    if (!uid) return "";
    return units.find((u) => u.id === uid)?.code ?? "";
  });

  // Expiry display
  const expiryDisplay = computed(() => {
    if (ls.draftExpiryMode.value !== "has" || !ls.draftExpiry.value) return "—";
    const d = new Date(ls.draftExpiry.value + "T00:00:00");
    return d.toLocaleDateString(undefined, { day: "numeric", month: "short" });
  });

  // Quick confirm: available when pending + matched + all required fields filled
  const canQuickConfirm = computed(() =>
    status === "Pending" && !unmatch &&
    !!ls.draftProductId.value &&
    parseFloat(ls.draftQty.value) > 0 &&
    !!ls.draftUnitId.value &&
    !!ls.draftLocationId.value
  );

  async function handleQuickConfirm() {
    if (ls.saving.value) return;
    ls.saving.value = true;
    ls.error.value = null;
    // Quick-confirm a matched line as-is — same single payload builder + submit path as the drawer
    // (createNew is false here, so buildSaveLineBody produces the confirm-existing body).
    await submitSaveLine(ls, buildSaveLineBody(ls), saveLineUrl, token, onSaved);
  }

  async function handleRestore() {
    if (ls.saving.value) return;
    ls.saving.value = true;
    try {
      const resp = await postJson(`${restoreLineUrl}&lineId=${ls.lineId}`, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) {
        ls.error.value = data.error ?? `Restore failed (${resp.status})`;
      } else {
        onRestored(ls);
      }
    } catch {
      ls.error.value = "Network error — please try again.";
    } finally {
      ls.saving.value = false;
    }
  }

  const rowId = `import-line-${ls.lineId}`;

  return html`
    <div id=${rowId}
         class=${"import-row " + stateClass + (ls.drawerOpen.value ? " import-row--open" : "")}
         data-status=${section.value}>

      ${ls.error.value && !ls.drawerOpen.value && html`
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
                    onClick=${handleRestore}>
              Add anyway
            </button>
          </div>
        `
        : html`
          <div class="import-row__main">
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
              ${isConfirmed && html`
                <span class="import-row__confirmed-flag">
                  <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> Matched
                </span>
              `}
              ${isCommitted && html`
                <span class="import-row__locked">
                  <svg class="icon" aria-hidden="true"><use href="#i-lock" /></svg> Added
                </span>
              `}
              ${!isConfirmed && !isCommitted && canQuickConfirm.value && html`
                <button type="button" class="btn btn--secondary btn--sm"
                        disabled=${ls.saving.value}
                        onClick=${handleQuickConfirm}>
                  <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
                  ${ls.saving.value ? "Saving…" : "Confirm"}
                </button>
              `}
              ${!isCommitted && html`
                <button type="button" class="import-row__toggle"
                        onClick=${() => { ls.drawerOpen.value = !ls.drawerOpen.value; }}
                        aria-expanded=${String(ls.drawerOpen.value)}
                        aria-label="Edit line">
                  <svg class=${"icon import-row__chev" + (ls.drawerOpen.value ? " import-row__chev--open" : "")}
                       aria-hidden="true">
                    <use href="#i-chevron" />
                  </svg>
                </button>
              `}
            </div>
          </div>

          ${ls.drawerOpen.value && !isCommitted && html`
            <${ReviewDrawer}
              ls=${ls}
              products=${products}
              units=${units}
              locations=${locations}
              categories=${categories}
              token=${token}
              saveLineUrl=${saveLineUrl}
              dismissLineUrl=${dismissLineUrl}
              sessionId=${sessionId}
              today=${today}
              onSaved=${onSaved}
              onDismissed=${onDismissed} />
          `}
        `
      }
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
 *   token: string,
 *   session: SessionHydration,
 *   filter: import("@preact/signals").Signal<string>,
 *   alert: import("@preact/signals").Signal<string>,
 *   onSaved: (ls: LineState, data: object) => void,
 *   onDismissed: (ls: LineState) => void,
 *   onRestored: (ls: LineState) => void,
 *   onCommit: () => void,
 *   onDiscard: () => void,
 * }} props
 */
function App({ lines, products, units, locations, categories, token, session, filter, alert, onSaved, onDismissed, onRestored, onCommit, onDiscard }) {
  const allLines = lines.value;

  // Derived chip counts
  const needsCount = computed(() => lines.value.filter((l) => lineSection(l) === "needs").length);
  const readyCount = computed(() => lines.value.filter((l) => lineSection(l) === "ready").length);
  const skippedCount = computed(() => lines.value.filter((l) => lineSection(l) === "skipped").length);
  const totalItems = computed(() => needsCount.value + readyCount.value);

  // Derived progress
  const canCommit = computed(() => needsCount.value === 0 && totalItems.value > 0);
  const progressPct = computed(() => totalItems.value > 0 ? Math.round(readyCount.value / totalItems.value * 100) : 100);

  // Commit bar: count + value of confirmed+committed non-dismissed lines
  const confirmedCount = computed(() =>
    lines.value.filter((l) => l.status.value === "Confirmed" || l.status.value === "Committed").length
  );
  const confirmedValue = computed(() =>
    lines.value
      .filter((l) => l.status.value === "Confirmed" || l.status.value === "Committed")
      .reduce((sum, l) => sum + (l.price.value ?? 0), 0)
  );
  // "to resolve" must derive from the SAME primitive the commit gate uses (needsCount), so the
  // displayed count and the disabled-button state can never disagree (they previously came from
  // two different definitions of "done" — committableCount - confirmedCount vs needsCount).
  const remaining = computed(() => needsCount.value);

  // Receipt total: non-dismissed lines
  const receiptTotal = computed(() =>
    lines.value
      .filter((l) => l.status.value !== "Dismissed")
      .reduce((sum, l) => sum + (l.price.value ?? 0), 0)
  );

  const needsMeta = computed(() => {
    if (canCommit.value) return `<span><b>All set.</b> ${totalItems.value} items ready to add.</span>`;
    return `<span><b>${needsCount.value}</b> ${needsCount.value === 1 ? "item needs" : "items need"} a quick look · <b>${readyCount.value}</b> ready</span>`;
  });

  const sectionRows = computed(() => ({
    needs: lines.value.filter((l) => lineSection(l) === "needs"),
    ready: lines.value.filter((l) => lineSection(l) === "ready"),
    skipped: lines.value.filter((l) => lineSection(l) === "skipped"),
  }));

  const showNeeds = filter.value === "all" || filter.value === "review";
  const showReady = filter.value === "all" || filter.value === "ready";
  const showSkipped = filter.value === "all";

  return html`
    <div class="review">
      <aside class="review__receipt rcpt-pane">
        <div class="rcpt-meta-top">
          <span class="rcpt-via">
            <svg class="icon" aria-hidden="true"><use href="#i-receipt" /></svg> Scanned receipt
          </span>
        </div>
        <div class="receipt">
          <div class="rcpt-store">
            <div class="rcpt-store-name">${session.merchantText || "Receipt"}</div>
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
            <div id="rcpt-total" class="rcpt-line rcpt-total">
              <span class="rl-name">TOTAL</span>
              <span class="rl-price">${receiptTotal.value.toFixed(2)}</span>
            </div>
          </div>
          <div class="rcpt-barcode"></div>
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
              ${lines.value.filter((l) => lineSection(l) === "needs" && showNeeds).map((ls) => html`
                <${ReviewRow} key=${ls.lineId} ls=${ls}
                  products=${products} units=${units} locations=${locations} categories=${categories}
                  token=${token} saveLineUrl=${session.saveLineUrl}
                  dismissLineUrl=${session.dismissLineUrl} restoreLineUrl=${session.restoreLineUrl}
                  sessionId=${session.sessionId} today=${session.today}
                  filter=${filter}
                  onSaved=${onSaved} onDismissed=${onDismissed} onRestored=${onRestored} />
              `)}

              ${showReady && sectionRows.value.ready.length > 0 && html`
                <div class="sec-label">
                  Matched &amp; ready <span class="sec-label__count">· ${sectionRows.value.ready.length}</span>
                </div>
              `}
              ${lines.value.filter((l) => lineSection(l) === "ready" && showReady).map((ls) => html`
                <${ReviewRow} key=${ls.lineId} ls=${ls}
                  products=${products} units=${units} locations=${locations} categories=${categories}
                  token=${token} saveLineUrl=${session.saveLineUrl}
                  dismissLineUrl=${session.dismissLineUrl} restoreLineUrl=${session.restoreLineUrl}
                  sessionId=${session.sessionId} today=${session.today}
                  filter=${filter}
                  onSaved=${onSaved} onDismissed=${onDismissed} onRestored=${onRestored} />
              `)}

              ${showSkipped && sectionRows.value.skipped.length > 0 && html`
                <div class="sec-label">
                  Skipped — not inventory <span class="sec-label__count">· ${sectionRows.value.skipped.length}</span>
                </div>
              `}
              ${lines.value.filter((l) => lineSection(l) === "skipped" && showSkipped).map((ls) => html`
                <${ReviewRow} key=${ls.lineId} ls=${ls}
                  products=${products} units=${units} locations=${locations} categories=${categories}
                  token=${token} saveLineUrl=${session.saveLineUrl}
                  dismissLineUrl=${session.dismissLineUrl} restoreLineUrl=${session.restoreLineUrl}
                  sessionId=${session.sessionId} today=${session.today}
                  filter=${filter}
                  onSaved=${onSaved} onDismissed=${onDismissed} onRestored=${onRestored} />
              `)}
            </div>
          `
        }

        <div id="commit-bar" class="review__commit">
          <div class="commit-bar">
            <button type="button" class="btn btn--ghost commit-bar__cancel"
                    onClick=${() => {
                      if (confirm("Discard this import? Nothing will be added to your pantry.")) {
                        onDiscard();
                      }
                    }}>
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
              Adding <b>${confirmedCount}</b> items ·
              <b class="mono">${confirmedValue.value.toLocaleString(undefined, { style: "currency", currency: "CAD", minimumFractionDigits: 2 })}</b>
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

  /**
   * Called after a successful saveLine: update the line's reactive state from the server response.
   * The server is authoritative (ADR-020 §2/§7).
   * @param {LineState} ls
   * @param {object} data
   */
  function onSaved(ls, data) {
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

  /** @param {LineState} ls */
  function onDismissed(ls) {
    batch(() => {
      ls.status.value = "Dismissed";
      ls.drawerOpen.value = false;
      ls.error.value = null;
    });
  }

  /** @param {LineState} ls */
  function onRestored(ls) {
    batch(() => {
      ls.status.value = "Pending";
      ls.error.value = null;
    });
  }

  async function onCommit() {
    try {
      const resp = await postJson(hydration.commitUrl, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) {
        alertMsg.value = data.error ?? `Commit failed (${resp.status})`;
        return;
      }
      if (data.redirectUrl) {
        window.location.href = data.redirectUrl;
      }
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
      if (data.redirectUrl) {
        window.location.href = data.redirectUrl;
      }
    } catch {
      alertMsg.value = "Network error — please try again.";
    }
  }

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
    /** @param {string} lineId */
    openDrawer(lineId) {
      const ls = linesSignal.value.find((l) => l.lineId === lineId);
      if (ls) ls.drawerOpen.value = true;
    },
  };

  render(
    html`<${App}
      lines=${linesSignal}
      products=${hydration.products}
      units=${hydration.units}
      locations=${hydration.locations}
      categories=${hydration.categories}
      token=${token}
      session=${hydration}
      filter=${filter}
      alert=${alertMsg}
      onSaved=${onSaved}
      onDismissed=${onDismissed}
      onRestored=${onRestored}
      onCommit=${onCommit}
      onDiscard=${onDiscard} />`,
    root,
  );
}
