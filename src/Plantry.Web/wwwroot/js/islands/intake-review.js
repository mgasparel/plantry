// @ts-check
//
// Intake Review island (ADR-020, bead plantry-2zvm.3 · deals-deck rewrite plantry-gpdb).
//
// Buildless Preact + htm + signals. The review surface follows the DEALS-DECK flow
// (epic plantry-wmgg, prototype .preview/intake-review-deck.html): one screen, four pools —
//   1. "Judgement calls" DECK — the canonical Deals `.focus-card` family (two-block evidence card,
//      ranked `.suggest-opts`, a qty/unit/location/expiry details strip, swipe stamps, skip-stack
//      rotation, an inline `.kbd-bar`). Enter/→ confirm · X/← reject · M change match · N new product ·
//      S skip · Z undo skip · U undo · 1-9 pick.
//   2. "Confirm the sure things" CHECKLIST — the `_ReviewStep1` `.check-list` family, pre-checked, with
//      an explicit "Confirm N matches" bulk action (ConfirmLines endpoint, checked ids only). Unchecking
//      a row demotes it into the deck with a synthesised "double-check the match" decision.
//   3. CONFIRMED list — `.import-row--confirmed` with an edit drawer (SaveLine; "Wrong product — review
//      again" reopens to the deck top; "Not pantry stock — remove" dismisses).
//   4. SKIPPED list — read-only `.check-row--skipped` rows with "Add anyway" (RestoreLine).
// A navigable receipt minimap + money reconciliation footer re-derive from these pools.
//
// ADR-020 §2 boundary:
//   SERVER: domain rules (prefill priority chain, validation, aggregates after save), persistence,
//           the bulk-confirm qualification predicate (ConfirmLines), commit/discard redirect target.
//   ISLAND: UI/draft state (deck order, skip stack, drawer open/closed, checkbox state, form fields),
//           and derived DISPLAY state (sections, counts, progress, commit bar, reconciliation) computed
//           via signals from the authoritative line states each JSON endpoint returns.
//   Every deck/checklist verb round-trips through a JSON endpoint (SaveLine / DismissLine / ConfirmLines
//   / ReopenLine / RestoreLine); the island owns order + drag + checkbox + focus, nothing more (§7).

// ── Cache-busting convention (plantry-hxkf) ───────────────────────────────────
//
// The server (Review.cshtml) versions this entry module via IFileVersionProvider (content hash).
// Transitive imports are NOT independently versioned by Razor — bump the ?v= query below when the
// imported module changes, so the browser re-fetches it (changing this file's bytes reloads the graph).
//   ./runtime.js?v=N              bump when runtime.js changes
//   ./intake-review-logic.js?v=N  bump when intake-review-logic.js changes
//   (intake-review-logic.js itself carries the ?v= token on its ./deal-deck-logic.js import.)

import { render, html, signal, computed, batch, useRef } from "./runtime.js?v=1";
import { readAntiforgeryToken, postJson } from "./helpers.js";
import {
  makeLine as makeLineFromSeed,
  lineSection,
  isSurePending,
  buildSaveLineBody,
  commitBarCounts,
  estimateHint,
  decisionVariant,
  deckReasoning,
  optionRankLabel,
  demotedDecision,
  railLineView,
  reconciliation,
  buildDeckOrder,
  applySkip,
  applyBack,
  reconcileSkipStack,
  nextBaseline,
  deckProgress,
  swipeVerb,
  stampOpacity,
  cardTransform,
  filterStores,
  buildCorrectHeaderBody,
} from "./intake-review-logic.js?v=5";

// ── Type documentation ───────────────────────────────────────────────────────

/**
 * @typedef {Object} PrefillData
 * @property {string|null} productId
 * @property {string|null} productName
 * @property {number|null} quantity
 * @property {string|null} unitId
 * @property {string|null} locationId
 * @property {number|null} price
 * @property {string|null} expiry
 * @property {string|null} skuId
 */

/**
 * @typedef {Object} LineSeed
 * @property {string} lineId
 * @property {string} receiptText
 * @property {string} confidence
 * @property {string} status
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
 * @property {string|null} expiry
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
 * @typedef {Object} StoreHydration
 * @property {string} id
 * @property {string} name
 */

/**
 * @typedef {Object} AlternativeHydration
 * @property {string} productId
 * @property {string} productName
 * @property {number} confidence
 */

/**
 * @typedef {Object} EstimateHydration
 * @property {number} eachCount
 * @property {number} weight
 * @property {string} weightUnit
 * @property {string} confidence
 */

/**
 * @typedef {Object} SessionHydration
 * @property {string} merchantText
 * @property {string} sessionDate
 * @property {string} today
 * @property {string} commitUrl
 * @property {string} discardUrl
 * @property {string} saveLineUrl
 * @property {string} dismissLineUrl
 * @property {string} restoreLineUrl
 * @property {string} reopenLineUrl
 * @property {string} confirmLinesUrl
 * @property {string} correctHeaderUrl
 * @property {ProductHydration[]} products
 * @property {UnitHydration[]} units
 * @property {LocationHydration[]} locations
 * @property {CategoryHydration[]} categories
 * @property {StoreHydration[]} stores
 * @property {Array<{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null, estimate: EstimateHydration|null}>} lines
 * @property {string} scanVia
 * @property {string} scannedLabel
 * @property {string|null} storeBranch
 * @property {string|null} purchaseDate
 * @property {string|null} purchaseTime
 * @property {string|null} merchantTextRaw
 * @property {string|null} selectedStoreId
 * @property {string|null} purchaseDateRaw
 * @property {string|null} purchaseTimeRaw
 * @property {number|null} subtotal
 * @property {number|null} tax
 * @property {number|null} total
 * @property {string|null} payment
 * @property {string|null} receiptNo
 * @property {string} currencySymbol
 */

/**
 * @typedef {import("./intake-review-logic.js").LineState} LineState
 */

// ── Line factory ─────────────────────────────────────────────────────────────

/**
 * @param {{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null, estimate: EstimateHydration|null}} seed
 * @returns {LineState}
 */
function makeLine(seed) {
  return makeLineFromSeed(seed, signal);
}

// ── small display helpers ──────────────────────────────────────────────────────

// Household display-currency symbol, injected once at mount from the server hydration payload
// (plantry-2x6e.3). Module-scoped because the two money formatters below are module-level and there is
// exactly one intake-review island per page; keeping it here avoids threading a prop through every deck /
// commit-bar component (this island's mount is a known complexity hotspot — the change stays surgical).
// Defaults to "$" defensively; the server always sends the symbol via MoneyDisplay.Symbol (no currency map in JS).
let moneySymbol = "$";

/** Confirmed-total / line money ("$3.99"), prefixed with the household currency symbol. @param {number} n */
const fmtMoney = (n) => moneySymbol + n.toFixed(2);
/** Receipt-facsimile money ("$3.99") — symbol prefix matching the rail's monospaced prices. @param {number} n */
const fmtRcpt = (n) => moneySymbol + n.toFixed(2);
/** @param {string} raw */
const prettyRaw = (raw) => raw.toLowerCase().replace(/\b\w/g, (c) => c.toUpperCase());

// ── DetailsStrip — the 4-up qty / unit / location / expiry strip on a deck card ──

/**
 * @param {{ ls: LineState, units: UnitHydration[], locations: LocationHydration[], today: string }} props
 */
function DetailsStrip({ ls, units, locations, today }) {
  return html`
    <div class="focus-card__details">
      <div class="form-grid__field">
        <label class="form-grid__field__label">Quantity</label>
        <div class="form-grid__field__control">
          <div class="stepper" role="group">
            <button type="button" class="stepper__btn" aria-label="Decrease quantity"
                    onClick=${() => { const v = parseFloat(ls.draftQty.value) || 0; ls.draftQty.value = String(Math.max(0.001, Math.round((v - 1) * 1000) / 1000)); }}>
              <svg class="icon" aria-hidden="true"><use href="#i-minus" /></svg>
            </button>
            <input class="stepper__val" type="number" step="any" min="0" name="Edit.Quantity"
                   value=${ls.draftQty}
                   onInput=${(/** @type {InputEvent} */ e) => { ls.draftQty.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
            <button type="button" class="stepper__btn" aria-label="Increase quantity"
                    onClick=${() => { const v = parseFloat(ls.draftQty.value) || 0; ls.draftQty.value = String(Math.round((v + 1) * 1000) / 1000); }}>
              <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>
            </button>
          </div>
        </div>
      </div>
      <div class="form-grid__field">
        <label class="form-grid__field__label">Unit</label>
        <div class="form-grid__field__control">
          <select class="field__input" name="Edit.UnitId" value=${ls.draftUnitId}
                  onChange=${(/** @type {Event} */ e) => { ls.draftUnitId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
            <option value="">— Unit —</option>
            ${units.map((u) => html`<option key=${u.id} value=${u.id}>${u.code} — ${u.name}</option>`)}
          </select>
        </div>
      </div>
      <div class="form-grid__field">
        <label class="form-grid__field__label">Location</label>
        <div class="form-grid__field__control">
          <select class="field__input" name="Edit.LocationId" value=${ls.draftLocationId}
                  onChange=${(/** @type {Event} */ e) => { ls.draftLocationId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
            <option value="">— Location —</option>
            ${locations.map((l) => html`<option key=${l.id} value=${l.id}>${l.name}</option>`)}
          </select>
        </div>
      </div>
      <div class="form-grid__field">
        <label class="form-grid__field__label">Expires</label>
        <div class="form-grid__field__control">
          ${ls.draftExpiryMode.value === "has"
            ? html`
              <input type="date" class="field__input" name="Edit.ExpiryDate" value=${ls.draftExpiry}
                     onInput=${(/** @type {InputEvent} */ e) => { ls.draftExpiry.value = /** @type {HTMLInputElement} */ (e.target).value; }} />`
            : html`
              <button type="button" class="btn btn--ghost btn--sm"
                      onClick=${() => { ls.draftExpiryMode.value = "has"; if (!ls.draftExpiry.value) ls.draftExpiry.value = today; }}>
                + Add expiry
              </button>`}
        </div>
      </div>
    </div>
  `;
}

// ── MatchBlock — the evidence "match" half of a deck card (match / create / search) ──

/**
 * Selects the nth (0-based) alternative for a line, mirroring a suggest-opt click. Shared by the
 * click handler and the 1–9 keyboard shortcut.
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
 *   ls: LineState, products: ProductHydration[], categories: CategoryHydration[],
 *   skuCount: number,
 * }} props
 */
function MatchBlock({ ls, products, categories, skuCount }) {
  // ── Search mode (M / "Change match") — inline searchable-select with a demoted "+ Create" escape. ──
  if (ls.searchOpen.value) {
    const q = ls.draftProductName.value.trim().toLowerCase();
    const hits = (q ? products.filter((p) => p.name.toLowerCase().includes(q)) : products).slice(0, 6);
    return html`
      <div class="focus-card__link">Match it yourself</div>
      <div class="focus-card__match focus-card__match--none">
        <div class="focus-card__src">Your catalog</div>
        <div class="searchable-select">
          <div class="searchable-select__control">
            <input type="text" class="field__input" id=${`fc-search-${ls.lineId}`} role="combobox" autocomplete="off"
                   aria-expanded="true" placeholder="Find a product…" value=${ls.draftProductName}
                   onInput=${(/** @type {InputEvent} */ e) => { ls.draftProductName.value = /** @type {HTMLInputElement} */ (e.target).value; ls.draftProductId.value = ""; }} />
          </div>
          <ul class="searchable-select__listbox searchable-select__listbox--inline" role="listbox">
            ${hits.map((p, i) => html`
              <li key=${p.id} role="option"
                  onMouseDown=${(/** @type {MouseEvent} */ e) => { e.preventDefault(); pickSearchResult(ls, p, products); }}>
                ${p.name}${q ? html`<span class="rk">${i === 0 ? "best" : Math.max(15, 80 - i * 14) + "%"}</span>` : ""}
              </li>`)}
          </ul>
          <button type="button" class=${"btn btn--secondary btn--sm searchable-select__create-btn" + (hits.length ? " btn--demoted" : "")}
                  onMouseDown=${(/** @type {MouseEvent} */ e) => { e.preventDefault(); enterCreate(ls); }}>
            + Create “${(ls.draftProductName.value.trim() || prettyRaw(ls.receiptText))}”
          </button>
        </div>
      </div>
    `;
  }

  // ── Create mode (N / no-match) — name + category, then the shared details strip. ──
  if (ls.createNew.value) {
    return html`
      <div class="focus-card__link">Plantry suggests</div>
      <div class="focus-card__match">
        <div class="focus-card__src">New catalog product</div>
        <div class="focus-card__details" style="grid-template-columns: 3fr 1fr; margin-top: var(--space-2)">
          <div class="form-grid__field">
            <label class="form-grid__field__label">Product name</label>
            <div class="form-grid__field__control">
              <input class="field__input" name="Edit.NewProductName" placeholder=${ls.receiptText} value=${ls.draftNewName}
                     onInput=${(/** @type {InputEvent} */ e) => { ls.draftNewName.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
            </div>
          </div>
          <div class="form-grid__field">
            <label class="form-grid__field__label">Category</label>
            <div class="form-grid__field__control">
              <select class="field__input" name="Edit.NewProductCategoryId" value=${ls.draftNewCategoryId}
                      onChange=${(/** @type {Event} */ e) => { ls.draftNewCategoryId.value = /** @type {HTMLSelectElement} */ (e.target).value; }}>
                <option value="">— Category —</option>
                ${categories.map((c) => html`<option key=${c.id} value=${c.id}>${c.name}</option>`)}
              </select>
            </div>
          </div>
        </div>
        <div class="focus-card__reasoning">${deckReasoning("create")}</div>
      </div>
    `;
  }

  // ── Match mode — the current product + (when present) ranked alternatives / demoted single option. ──
  const variant = decisionVariant(ls, skuCount);
  const demoted = ls.demoted.value;
  const currentName = displayNameFor(ls, products);

  /** @type {Array<{ productId: string|null, label: string, confidence: number, recommended: boolean }>} */
  let options;
  let reasoning;
  if (demoted) {
    const d = demotedDecision(currentName, ls.draftProductId.value || null, (ls.confidence === "High" ? 1 : 0));
    options = [d.option];
    reasoning = d.reasoning;
  } else if (ls.alternatives && ls.alternatives.length >= 2) {
    options = ls.alternatives.map((a, i) => ({
      productId: a.productId, label: a.productName, confidence: a.confidence, recommended: i === 0,
    }));
    reasoning = deckReasoning("match");
  } else {
    options = [];
    reasoning = ls.estimate ? (estimateHint(ls.estimate) ?? deckReasoning(variant)) : deckReasoning(variant);
  }

  return html`
    <div class="focus-card__link">Plantry thinks this is</div>
    <div class="focus-card__match">
      <div class="focus-card__src">Your catalog</div>
      <div class="focus-card__product">${currentName || "Unrecognized item"}</div>
      ${reasoning ? html`<div class="focus-card__reasoning">${reasoning}</div>` : ""}
      ${options.length > 1 && html`
        <div class="suggest-opts">
          ${options.map((o, k) => {
            const selected = !!o.productId && ls.draftProductId.value === o.productId;
            const rk = optionRankLabel(k, o.confidence, o.recommended);
            return html`
              <button key=${o.productId ?? k} type="button" class=${"suggest-opt" + (selected ? " sel" : "")}
                      onClick=${() => pickAlternative(ls, k)}>
                ${selected && html`<svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>`}
                ${o.label}
                <kbd>${k + 1}</kbd>${rk ? html`<span class="rk">${rk}</span>` : ""}
              </button>`;
          })}
        </div>`}
    </div>
  `;
}

/** Resolve a line's display product name from its draft product or create-name. @param {LineState} ls @param {ProductHydration[]} products */
function displayNameFor(ls, products) {
  if (ls.createNew.value) return ls.draftNewName.value || ls.receiptText;
  const pid = ls.draftProductId.value;
  if (pid) {
    const p = products.find((x) => x.id === pid);
    if (p) return p.name;
  }
  return ls.draftProductName.value || "";
}

// These are bound to mount-scope handlers at render time (see mountIntakeReview). Declared here so the
// components above can reference them; assigned once on mount.
/** @type {(ls: LineState) => void} */
let enterCreate = () => {};
/** @type {(ls: LineState, product: ProductHydration, products: ProductHydration[]) => void} */
let pickSearchResult = () => {};

// ── DeckCard — the top judgement-call card (.focus-card family) ──────────────────

/**
 * @param {{
 *   ls: LineState, products: ProductHydration[], units: UnitHydration[], locations: LocationHydration[],
 *   categories: CategoryHydration[], today: string, canSkip: boolean, canBack: boolean,
 *   onConfirm: () => void, onReject: () => void, onSkip: () => void, onBack: () => void,
 *   onSearchOn: (ls: LineState) => void, onSearchOff: (ls: LineState) => void,
 * }} props
 */
function DeckCard({ ls, products, units, locations, categories, today, canSkip, canBack, onConfirm, onReject, onSkip, onBack, onSearchOn, onSearchOff }) {
  const skuCount = (() => {
    const pid = ls.draftProductId.value;
    return products.find((p) => p.id === pid)?.skus?.length ?? 0;
  })();
  const isCreateish = ls.createNew.value;
  const inSearch = ls.searchOpen.value;

  // Pointer-swipe wiring (mirrors the Deals deck geometry via deal-deck-logic). The card element and
  // its transient drag distance live in refs so a pointermove mutates style imperatively without a
  // Preact re-render per event; crossing the threshold fires the same confirm/reject verb as the buttons.
  const cardRef = useRef(/** @type {HTMLElement|null} */ (null));
  const drag = useRef({ startX: /** @type {number|null} */ (null), dx: 0 });

  /** @param {PointerEvent} e */
  function onPointerDown(e) {
    if (/** @type {HTMLElement} */ (e.target).closest("button, a, input, select, label, kbd, .suggest-opt")) return;
    drag.current.startX = e.clientX;
    drag.current.dx = 0;
    cardRef.current?.classList.add("dragging");
    try { cardRef.current?.setPointerCapture(e.pointerId); } catch { /* ignore */ }
  }
  /** @param {PointerEvent} e */
  function onPointerMove(e) {
    if (drag.current.startX === null || !cardRef.current) return;
    const dx = e.clientX - drag.current.startX;
    drag.current.dx = dx;
    cardRef.current.style.transform = cardTransform(dx);
    const op = stampOpacity(dx);
    const c = cardRef.current.querySelector('[data-hint="confirm"]');
    const r = cardRef.current.querySelector('[data-hint="reject"]');
    if (c) /** @type {HTMLElement} */ (c).style.opacity = String(op.confirm);
    if (r) /** @type {HTMLElement} */ (r).style.opacity = String(op.reject);
  }
  function onPointerEnd() {
    if (drag.current.startX === null || !cardRef.current) return;
    const dx = drag.current.dx;
    drag.current.startX = null;
    const el = cardRef.current;
    el.classList.remove("dragging");
    // ALWAYS settle the card back to rest BEFORE firing a verb. The verb (deckConfirm / deckReject) is
    // async and can fail — deckConfirm runs validateLine, which rejects an incomplete deck card (missing
    // unit/location/name+category) and leaves the line in `needs`, so Preact reuses this same DOM node
    // (key = unchanged lineId) and any lingering imperative transform would strand the card off-screen
    // with its error rendered out of view. On a successful verb the node unmounts on the next render, so
    // the brief settle is invisible.
    el.classList.add("springing");
    el.style.transform = "";
    const c = el.querySelector('[data-hint="confirm"]');
    const r = el.querySelector('[data-hint="reject"]');
    if (c) /** @type {HTMLElement} */ (c).style.opacity = "0";
    if (r) /** @type {HTMLElement} */ (r).style.opacity = "0";
    setTimeout(() => el.classList.remove("springing"), 200);
    const verb = swipeVerb(dx);
    if (verb === "confirm") onConfirm();
    else if (verb === "reject") onReject();
  }

  return html`
    <section class="card focus-card" ref=${cardRef} data-line-id=${ls.lineId}
             onPointerDown=${onPointerDown} onPointerMove=${onPointerMove}
             onPointerUp=${onPointerEnd} onPointerCancel=${onPointerEnd}>
      <span class="swipe-hint swipe-hint--confirm" data-hint="confirm">✓ Confirm</span>
      <span class="swipe-hint swipe-hint--reject" data-hint="reject">✕ Not stock</span>

      <div class="focus-card__flyer">
        <div class="focus-card__src">From the receipt</div>
        <div class="focus-card__raw-name"><span class="deal-review-row__name" title=${ls.receiptText}>${ls.receiptText}</span></div>
        <div class="focus-card__meta"><span class="deal-row__amount">${ls.price.value != null ? fmtRcpt(ls.price.value) : ""}</span></div>
      </div>

      ${ls.error.value && html`
        <div class="import-row__error" role="alert">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> ${ls.error.value}
        </div>`}

      <${MatchBlock} ls=${ls} products=${products} categories=${categories} skuCount=${skuCount} />
      ${!inSearch && html`<${DetailsStrip} ls=${ls} units=${units} locations=${locations} today=${today} />`}

      <div class="focus-verbs">
        ${inSearch
          ? html`<button type="button" class="btn btn--ghost btn--sm" onClick=${() => onSearchOff(ls)}>← Back to the suggestion</button>`
          : html`
            <button type="button" class="btn btn--ghost btn--sm" disabled=${ls.saving.value} onClick=${onReject}>Not pantry stock</button>
            <button type="button" class="btn btn--secondary btn--sm" onClick=${() => onSearchOn(ls)}>
              <svg class="icon" aria-hidden="true"><use href="#i-search" /></svg> Change match
            </button>
            <button type="button" class="btn btn--primary btn--sm" disabled=${ls.saving.value} onClick=${onConfirm}>
              <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> ${isCreateish ? "Add new & next" : "Confirm & next"}
            </button>`}
      </div>

      <div class="focus-under">
        <button type="button" disabled=${!canBack} onClick=${onBack}>← Undo skip</button>
        <button type="button" disabled=${!canSkip} onClick=${onSkip}>Skip for now →</button>
      </div>
    </section>
  `;
}

// ── Checklist — the "sure things" pool (.check-list / .step-foot) ────────────────

/**
 * @param {{ sureLines: LineState[], products: ProductHydration[], onToggle: (ls: LineState) => void, onBulkConfirm: () => void, onFlashJump: (lineId: string) => void }} props
 */
function Checklist({ sureLines, products, onToggle, onBulkConfirm }) {
  const picked = sureLines.filter((l) => l.checked.value).length;
  return html`
    <div class="sec-label">Confirm the sure things <span class="sec-label__count">· ${sureLines.length}</span></div>
    <section class="card card--flow">
      <ul class="check-list" role="list">
        ${sureLines.map((ls) => html`
          <li key=${ls.lineId} class="check-row" data-line-id=${ls.lineId}
              onClick=${(/** @type {MouseEvent} */ e) => { if (/** @type {HTMLElement} */ (e.target).tagName !== "INPUT") onToggle(ls); }}>
            <input type="checkbox" checked=${ls.checked.value}
                   onChange=${() => onToggle(ls)} />
            <span class="raw"><b title=${ls.receiptText}>${prettyRaw(ls.receiptText)}</b></span>
            <span class="to">→ <span class="product">${displayNameFor(ls, products)}</span></span>
          </li>`)}
      </ul>
      <div class="step-foot">
        <span class="note">Anything you uncheck joins the judgement calls above — nothing is lost.</span>
        <button type="button" class="btn btn--primary" onClick=${onBulkConfirm}>
          <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> Confirm ${picked} ${picked === 1 ? "match" : "matches"}
        </button>
      </div>
    </section>
  `;
}

// ── ConfirmedRow — a committed-to-staging row (.import-row--confirmed) + edit drawer ──

/**
 * @param {{
 *   ls: LineState, products: ProductHydration[], units: UnitHydration[], locations: LocationHydration[],
 *   today: string, onSaveEdit: (ls: LineState) => void, onRematch: (ls: LineState) => void, onReject: (ls: LineState) => void,
 * }} props
 */
function ConfirmedRow({ ls, products, units, locations, today, onSaveEdit, onRematch, onReject }) {
  const open = ls.drawerOpen.value;
  const isCommitted = ls.status.value === "Committed";
  const name = displayNameFor(ls, products);
  const qty = (() => { const n = parseFloat(ls.draftQty.value); return isNaN(n) ? "—" : n.toLocaleString(undefined, { maximumFractionDigits: 3 }); })();
  const unit = units.find((u) => u.id === ls.draftUnitId.value)?.code ?? "";
  const price = ls.price.value != null ? fmtMoney(ls.price.value) : "—";
  const expiry = ls.draftExpiryMode.value === "has" && ls.draftExpiry.value
    ? new Date(ls.draftExpiry.value + "T00:00:00").toLocaleDateString(undefined, { day: "numeric", month: "short" }) : "—";

  return html`
    <div class=${"import-row import-row--confirmed" + (open ? " import-row--open" : "")}>
      <div class="import-row__main"
           data-action=${isCommitted ? undefined : "toggle-edit"}
           onClick=${isCommitted ? undefined : () => { ls.drawerOpen.value = !ls.drawerOpen.value; }}
           style=${isCommitted ? "" : "cursor:pointer"}>
        <div class="import-row__id">
          <div class="import-row__name">
            <span class="import-row__product">${name || "Unrecognized item"}</span>
            ${ls.isNewProduct && html`<span class="import-row__new"> · new product</span>`}
          </div>
          <div class="import-row__raw"><span class="import-row__raw-tag">receipt</span><span>${ls.receiptText}</span></div>
        </div>
        <div class="import-row__meta">
          <div class="import-row__meta-cell">
            <div class="import-row__meta-value">${qty}<span class="import-row__meta-unit"> ${unit}</span></div>
            <div class="import-row__meta-label">qty</div>
          </div>
          <div class="import-row__meta-cell">
            <div class="import-row__meta-value">${price}</div>
            <div class="import-row__meta-label">price</div>
          </div>
          <div class="import-row__meta-cell">
            <div class="import-row__meta-value">${expiry}</div>
            <div class="import-row__meta-label">expires</div>
          </div>
        </div>
        <div class="import-row__act">
          ${isCommitted
            ? html`<span class="import-row__locked"><svg class="icon" aria-hidden="true"><use href="#i-lock" /></svg> Added</span>`
            : html`
              <span class="import-row__confirmed-flag"><svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> Ready</span>
              <button type="button" class="import-row__toggle" aria-expanded=${String(open)} aria-label="Edit line"
                      onClick=${(/** @type {Event} */ e) => { e.stopPropagation(); ls.drawerOpen.value = !ls.drawerOpen.value; }}>
                <svg class=${"icon import-row__chev" + (open ? " import-row__chev--open" : "")} aria-hidden="true"><use href="#i-chevron" /></svg>
              </button>`}
        </div>
      </div>

      ${open && !isCommitted && html`
        <div class="import-row__edit review-row__edit">
          ${ls.error.value && html`<div class="import-row__error" role="alert"><svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> ${ls.error.value}</div>`}
          <form onSubmit=${(/** @type {Event} */ e) => { e.preventDefault(); onSaveEdit(ls); }}>
            <${DetailsStrip} ls=${ls} units=${units} locations=${locations} today=${today} />
            <div class="import-row__edit-foot">
              <div class="import-row__edit-foot-summary">
                <svg class="icon" aria-hidden="true"><use href="#i-location" /></svg>
                Goes to <strong>${locations.find((l) => l.id === ls.draftLocationId.value)?.name ?? "—"}</strong>
              </div>
              <span class="import-row__edit-spacer"></span>
              <button type="button" class="btn btn--ghost btn--sm" disabled=${ls.saving.value} onClick=${() => onRematch(ls)}>Wrong product — review again</button>
              <button type="button" class="btn btn--ghost btn--sm import-row__edit-danger" disabled=${ls.saving.value} onClick=${() => onReject(ls)}>Not pantry stock — remove</button>
              <button type="submit" class="btn btn--primary btn--sm" disabled=${ls.saving.value}>
                <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> ${ls.saving.value ? "Saving…" : "Save changes"}
              </button>
            </div>
          </form>
        </div>`}
    </div>
  `;
}

// ── HeaderPanel — the receipt-header store / date-time correction (plantry-yobz) ──
//
// The parsed merchant + purchase date/time render as LOCKED values with a "Change" affordance, mirroring
// the deck's MatchBlock pattern (searchable-select over the household's active stores + a demoted "+ Create"
// escape). All edits round-trip through the CorrectHeader JSON endpoint; the island owns only the open/closed
// editor state and the draft field values (ADR-020 §2/§7 — the server re-validates and echoes back the
// locked truth). An unresolved store or a guard-nulled date arrives empty and prompts entry, never a stale
// locked AI guess.

/** @param {{ header: any, stores: StoreHydration[], handlers: any }} props */
function StoreEditor({ header, stores, handlers }) {
  const hits = filterStores(stores, header.draftMerchant.value);
  const typed = header.draftMerchant.value.trim();
  return html`
    <div class="searchable-select rcpt-header-edit">
      <div class="searchable-select__control">
        <input type="text" class="field__input" role="combobox" autocomplete="off" aria-expanded="true"
               id="rcpt-store-search" placeholder="Find a store…" value=${header.draftMerchant}
               onInput=${(/** @type {InputEvent} */ e) => { header.draftMerchant.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
      </div>
      ${hits.length > 0 && html`
        <ul class="searchable-select__listbox searchable-select__listbox--inline" role="listbox">
          ${hits.map((s) => html`
            <li key=${s.id} role="option"
                onMouseDown=${(/** @type {MouseEvent} */ e) => { e.preventDefault(); handlers.headerPickStore(s); }}>${s.name}</li>`)}
        </ul>`}
      <div class="rcpt-header-edit__acts">
        <button type="button" class=${"btn btn--secondary btn--sm searchable-select__create-btn" + (hits.length ? " btn--demoted" : "")}
                disabled=${header.saving.value}
                onMouseDown=${(/** @type {MouseEvent} */ e) => { e.preventDefault(); handlers.headerCreateStore(); }}>
          + Create “${typed || "new store"}”
        </button>
        <button type="button" class="btn btn--ghost btn--sm" onClick=${() => handlers.headerCancelStore()}>Cancel</button>
      </div>
    </div>`;
}

/** @param {{ header: any, handlers: any }} props */
function DateEditor({ header, handlers }) {
  return html`
    <div class="rcpt-header-edit rcpt-header-edit--date">
      <div class="rcpt-header-edit__fields">
        <input type="date" class="field__input" aria-label="Purchase date" value=${header.draftDate}
               onInput=${(/** @type {InputEvent} */ e) => { header.draftDate.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
        <input type="time" class="field__input" aria-label="Purchase time" value=${header.draftTime}
               onInput=${(/** @type {InputEvent} */ e) => { header.draftTime.value = /** @type {HTMLInputElement} */ (e.target).value; }} />
      </div>
      <div class="rcpt-header-edit__acts">
        <button type="button" class="btn btn--ghost btn--sm" onClick=${() => handlers.headerCancelDate()}>Cancel</button>
        <button type="button" class="btn btn--primary btn--sm" disabled=${header.saving.value} onClick=${() => handlers.headerSaveDate()}>Save</button>
      </div>
    </div>`;
}

/** @param {{ header: any, stores: StoreHydration[], storeBranch: string|null, handlers: any }} props */
function HeaderPanel({ header, stores, storeBranch, handlers }) {
  const merchant = header.merchantText.value;
  const dateDisp = header.purchaseDateDisplay.value;
  const timeDisp = header.purchaseTimeDisplay.value;
  const dtDisp = [dateDisp, timeDisp].filter(Boolean).join(" · ");

  return html`
    <div class="rcpt-store">
      ${header.storeEditing.value
        ? html`<${StoreEditor} header=${header} stores=${stores} handlers=${handlers} />`
        : html`
          <div class="rcpt-store-name">
            <span>${merchant || "Receipt"}</span>
            <button type="button" class="rcpt-edit-btn" onClick=${() => handlers.headerEditStore()}>
              ${merchant ? "Change" : "Add store"}
            </button>
          </div>`}

      <div class="rcpt-store-sub">
        ${storeBranch && html`<div>${storeBranch}</div>`}
        ${header.dateEditing.value
          ? html`<${DateEditor} header=${header} handlers=${handlers} />`
          : html`
            <div class="rcpt-store-datetime">
              <span>${dtDisp || "No purchase date"}</span>
              <button type="button" class="rcpt-edit-btn" onClick=${() => handlers.headerEditDate()}>
                ${dateDisp ? "Change" : "Add date"}
              </button>
            </div>`}
      </div>

      ${header.error.value && html`
        <div class="rcpt-header-error" role="alert">
          <svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> ${header.error.value}
        </div>`}
    </div>`;
}

// ── App ──────────────────────────────────────────────────────────────────────

/**
 * @param {{
 *   lines: import("@preact/signals").Signal<LineState[]>,
 *   order: import("@preact/signals").Signal<string[]>,
 *   skipStack: import("@preact/signals").Signal<string[]>,
 *   baseline: import("@preact/signals").Signal<number>,
 *   products: ProductHydration[], units: UnitHydration[], locations: LocationHydration[], categories: CategoryHydration[],
 *   session: SessionHydration,
 *   header: any,
 *   alert: import("@preact/signals").Signal<string>,
 *   toastMsg: import("@preact/signals").Signal<string>,
 *   toastUndo: import("@preact/signals").Signal<boolean>,
 *   handlers: any,
 * }} props
 */
function App({ lines, order, skipStack, baseline, products, units, locations, categories, session, header, alert, toastMsg, toastUndo, handlers }) {
  const allLines = lines.value;
  const byId = (/** @type {string} */ id) => lines.value.find((l) => l.lineId === id) ?? null;

  const bar = computed(() => commitBarCounts(lines.value.map(lineSection)));
  const sureLines = computed(() => lines.value.filter((l) => lineSection(l) === "sure"));
  const confirmedLines = computed(() => lines.value.filter((l) => lineSection(l) === "confirmed"));
  const skippedLines = computed(() => lines.value.filter((l) => lineSection(l) === "skipped"));
  const deckIds = computed(() => order.value.filter((id) => { const l = byId(id); return l && lineSection(l) === "needs"; }));
  const topCard = computed(() => byId(deckIds.value[0]));

  const recon = computed(() =>
    reconciliation(lines.value.map((l) => ({ section: lineSection(l), price: l.price.value })), session.tax, session.total));

  const meterMeta = computed(() => {
    const b = bar.value;
    if (b.unresolved === 0) return `<span><b>All set.</b> ${b.confirmedCount} items ready to add.</span>`;
    const parts = [];
    if (b.sureCount) parts.push(`<b>${b.sureCount}</b> sure ${b.sureCount === 1 ? "thing" : "things"}`);
    if (b.needsCount) parts.push(`<b>${b.needsCount}</b> judgement ${b.needsCount === 1 ? "call" : "calls"}`);
    return `<span>${parts.join(" · ")} left</span>`;
  });

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
          <${HeaderPanel} header=${header} stores=${session.stores} storeBranch=${session.storeBranch} handlers=${handlers} />
          <hr class="rcpt-rule" />
          ${allLines.map((ls) => {
            const view = railLineView(lineSection(ls), topCard.value?.lineId === ls.lineId);
            return html`
              <div key=${ls.lineId}
                   class=${"rcpt-line rcpt-line--jump" + (view.done ? " rcpt-line--done" : "") + (view.dim ? " dim" : "") + (view.active ? " rcpt-line--active" : "")}
                   role="button" tabindex="0" aria-label=${`Jump to ${ls.receiptText}`}
                   onClick=${() => handlers.railJump(ls.lineId)}
                   onKeyDown=${(/** @type {KeyboardEvent} */ e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); handlers.railJump(ls.lineId); } }}>
                <span class="rcpt-line__st">${
                  view.glyph === "tick" ? html`<span class="tick" aria-hidden="true">✓</span>`
                  : view.glyph === "dot" ? html`<span class="dot" aria-hidden="true"></span>` : ""}</span>
                <span class="rl-name">${ls.receiptText}</span>
                <span class="rl-price">${ls.price.value != null ? ls.price.value.toFixed(2) : ""}</span>
              </div>`;
          })}
          <hr class="rcpt-rule" />
          <div class="rcpt-foot">
            ${session.subtotal != null && html`<div class="rcpt-line"><span class="rcpt-line__st"></span><span class="rl-name">SUBTOTAL</span><span class="rl-price">${session.subtotal.toFixed(2)}</span></div>`}
            ${session.tax != null && html`<div class="rcpt-line"><span class="rcpt-line__st"></span><span class="rl-name">TAX</span><span class="rl-price">${session.tax.toFixed(2)}</span></div>`}
            <div id="rcpt-total" class="rcpt-line rcpt-total"><span class="rcpt-line__st"></span><span class="rl-name">TOTAL</span><span class="rl-price">${(session.total != null ? session.total : recon.value.pantry + recon.value.undecided + recon.value.skippedFees).toFixed(2)}</span></div>
            ${session.payment && html`<div class="rcpt-line" style="margin-top:6px"><span class="rcpt-line__st"></span><span class="rl-name">${session.payment}</span></div>`}
          </div>
          <div class="rcpt-recon" id="rcpt-recon">
            <b>${fmtRcpt(recon.value.pantry)}</b> going to pantry${
              recon.value.undecided > 0 ? html` · <b>${fmtRcpt(recon.value.undecided)}</b> undecided` : ""}${
              recon.value.skippedFees > 0 ? html` · ${fmtRcpt(recon.value.skippedFees)} fees skipped` : ""}${
              recon.value.tax != null ? html` · ${fmtRcpt(recon.value.tax)} tax` : ""}${
              recon.value.total != null ? html`<br />= <b>${fmtRcpt(recon.value.total)}</b> receipt total${recon.value.reconciles ? " ✓" : ""}` : ""}
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
              <p>${header.merchantText.value || "Receipt"} · ${session.sessionDate} · ${allLines.length} lines scanned</p>
            </div>
          </div>
          <div class="meter meter--intake-progress meter--meta-line">
            <div class="meter__track"><div class="meter__fill meter__fill--accent" style=${`width: ${bar.value.progressPct}%`}></div></div>
            <div class="meter__meta" dangerouslySetInnerHTML=${{ __html: meterMeta.value }}></div>
          </div>
        </div>

        <div class="rev-list">
          ${(() => {
            const n = deckIds.value.length;
            return html`
              <div class="sec-label">Judgement calls <span class="sec-label__count">· ${n}</span></div>
              ${n === 0
                ? html`
                  <section class="card">
                    <div class="step-empty">
                      <div class="t">✓ All judgement calls resolved</div>
                      <p>Confirm the sure things below, then add everything to your pantry.</p>
                    </div>
                  </section>`
                : (() => {
                    const ls = topCard.value;
                    if (!ls) return "";
                    const prog = deckProgress(n, baseline.value);
                    return html`
                      <div class="deck-progress">
                        <div class="bar"><div class="fill" style=${`width:${prog.percent}%`}></div></div>
                        <span class="label">${prog.left} left</span>
                      </div>
                      <div class="deck-zone">
                        <${DeckCard} key=${ls.lineId} ls=${ls}
                          products=${products} units=${units} locations=${locations} categories=${categories} today=${session.today}
                          canSkip=${n > 1} canBack=${skipStack.value.length > 0}
                          onConfirm=${() => handlers.deckConfirm(ls)} onReject=${() => handlers.deckReject(ls)}
                          onSkip=${handlers.deckSkip} onBack=${handlers.deckBack}
                          onSearchOn=${handlers.searchOn} onSearchOff=${handlers.searchOff} />
                        <div class="swipe-tip">Swipe right to confirm · left to reject</div>
                      </div>
                      <div class="kbd-bar kbd-bar--inline">
                        <span><kbd>Enter</kbd>/<kbd>→</kbd> confirm</span>
                        <span><kbd>1</kbd>–<kbd>9</kbd> pick</span>
                        <span><kbd>M</kbd> change match</span>
                        <span><kbd>N</kbd> new product</span>
                        <span><kbd>X</kbd>/<kbd>←</kbd> not stock</span>
                        <span><kbd>S</kbd> skip</span>
                        <span><kbd>Z</kbd> undo skip</span>
                        <span><kbd>U</kbd> undo</span>
                      </div>`;
                  })()}`;
          })()}

          ${sureLines.value.length > 0 && html`
            <${Checklist} sureLines=${sureLines.value} products=${products}
              onToggle=${handlers.toggleCheck} onBulkConfirm=${handlers.bulkConfirm} onFlashJump=${handlers.railJump} />`}

          ${confirmedLines.value.length > 0 && html`
            <div class="sec-label">Confirmed <span class="sec-label__count">· ${confirmedLines.value.length} · ${fmtMoney(confirmedLines.value.reduce((s, l) => s + (l.price.value ?? 0), 0))}</span></div>
            ${confirmedLines.value.map((ls) => html`
              <${ConfirmedRow} key=${ls.lineId} ls=${ls} products=${products} units=${units} locations=${locations} today=${session.today}
                onSaveEdit=${handlers.saveEdit} onRematch=${handlers.rematch} onReject=${handlers.rowReject} />`)}`}

          ${skippedLines.value.length > 0 && html`
            <div class="sec-label">Skipped — not inventory <span class="sec-label__count">· ${skippedLines.value.length}</span></div>
            <section class="card card--flow">
              <ul class="check-list" role="list">
                ${skippedLines.value.map((ls) => html`
                  <li key=${ls.lineId} class="check-row check-row--skipped">
                    <span class="raw"><b title=${ls.receiptText}>${prettyRaw(ls.receiptText)}</b></span>
                    <span class="to">not inventory · ${ls.price.value != null ? fmtRcpt(ls.price.value) : "—"}</span>
                    <button type="button" class="btn btn--ghost btn--xs" disabled=${ls.saving.value} onClick=${() => handlers.restore(ls)}>Add anyway</button>
                  </li>`)}
              </ul>
            </section>`}
        </div>

        <div id="commit-bar" class="review__commit">
          <div class="commit-bar">
            <button type="button" class="btn btn--ghost commit-bar__cancel" onClick=${handlers.discard}>Cancel</button>
            <span class="commit-bar__spacer"></span>
            ${bar.value.remaining > 0 && html`
              <span class="commit-bar__warn"><svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> ${bar.value.remaining} to resolve</span>`}
            <div class="commit-bar__summary">Adding <b>${bar.value.confirmedCount}</b> items · <b class="mono">${fmtMoney(confirmedLines.value.reduce((s, l) => s + (l.price.value ?? 0), 0))}</b></div>
            <button type="button" class="btn btn--primary" disabled=${!bar.value.canCommit} onClick=${handlers.commit}>
              <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> Add to pantry
            </button>
          </div>
        </div>
      </section>

      ${toastMsg.value && html`
        <div class="toast" role="status" aria-live="polite"
             onClick=${(/** @type {MouseEvent} */ e) => { if (!(/** @type {HTMLElement} */ (e.target).closest("[data-action]"))) handlers.dismissToast(); }}>
          <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
          <span>${toastMsg.value}</span>
          ${toastUndo.value && html`<button type="button" class="toast__action" data-action="undo" onClick=${handlers.undo}>Undo</button>`}
        </div>`}
    </div>
  `;
}

// ── Mount ───────────────────────────────────────────────────────────────────

/**
 * @param {Element} root
 * @param {SessionHydration} hydration
 */
export function mountIntakeReview(root, hydration) {
  // Seed the module-scoped currency symbol from the server payload before any render (plantry-2x6e.3);
  // "$" is a defensive fallback only — the server always sends it.
  moneySymbol = hydration.currencySymbol ?? "$";

  const token = readAntiforgeryToken();
  const linesSignal = signal(hydration.lines.map(makeLine));

  // A still-Pending line with no product match AND no catalog alternatives is a create-only decision —
  // there is nothing to match to. Pre-open its create form so the deck card renders name + category
  // fields directly (the deck's create variant), mirroring the prototype's `decision.type === "create"`.
  // A line that DOES carry a product (e.g. a High match missing only a location) stays a match card; the
  // user can still switch a create card to search ("Change match") to pick an existing product instead.
  for (const l of linesSignal.value) {
    if (l.status.value === "Pending" && !l.isNewProduct && !l.draftProductId.value
        && (!l.alternatives || l.alternatives.length === 0)) {
      l.createNew.value = true;
    }
  }

  const alertMsg = signal("");
  const toastMsg = signal("");
  const toastUndo = signal(false);

  // ── Receipt-header correction state (plantry-yobz) ───────────────────────────
  // Locked display signals seed from the server hydration (merchantTextRaw is the un-"Receipt"-defaulted
  // value; empty means the AI read no store, so the picker prompts entry). Draft signals hold the in-flight
  // edit; both editors post the FULL header through CorrectHeader, so a store edit preserves the date and
  // vice versa. On success the server echo re-locks these from truth.
  const header = {
    storeEditing: signal(false),
    dateEditing: signal(false),
    saving: signal(false),
    error: signal(""),
    // Locked / committed display
    merchantText: signal(hydration.merchantTextRaw ?? ""),
    selectedStoreId: signal(hydration.selectedStoreId ?? ""),
    purchaseDateDisplay: signal(hydration.purchaseDate ?? ""),
    purchaseTimeDisplay: signal(hydration.purchaseTime ?? ""),
    purchaseDateRaw: signal(hydration.purchaseDateRaw ?? ""),
    purchaseTimeRaw: signal(hydration.purchaseTimeRaw ?? ""),
    // Draft (edit-mode) fields
    draftMerchant: signal(""),
    draftDate: signal(hydration.purchaseDateRaw ?? ""),
    draftTime: signal(hydration.purchaseTimeRaw ?? ""),
  };

  // Deck presentation state (order + skip stack + high-water baseline) — mirrors the Deals deck.
  const order = signal(/** @type {string[]} */ ([]));
  const skipStack = signal(/** @type {string[]} */ ([]));
  const baseline = signal(0);

  /** @type {{ fn: (() => void | Promise<void>) | null }} */
  const undoRef = { fn: null };
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let toastTimer;

  const byId = (/** @type {string} */ id) => linesSignal.value.find((l) => l.lineId === id) ?? null;
  const needsIds = () => linesSignal.value.filter((l) => lineSection(l) === "needs").map((l) => l.lineId);

  /** Rebuild the deck order from the current needs pool, preserving skip-rotation order, and grow the baseline. */
  function syncDeck() {
    const next = buildDeckOrder(needsIds(), order.value);
    batch(() => {
      order.value = next;
      skipStack.value = reconcileSkipStack(skipStack.value, next);
      baseline.value = nextBaseline(baseline.value, next.length);
    });
  }
  syncDeck();

  // ── toast ──────────────────────────────────────────────────────────────────
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

  /** @param {LineState} ls */
  function displayName(ls) {
    if (ls.isNewProduct && ls.newProductName) return ls.newProductName;
    if (ls.createNew.value && ls.draftNewName.value.trim()) return ls.draftNewName.value.trim();
    const pid = ls.draftProductId.value;
    if (pid) { const p = hydration.products.find((x) => x.id === pid); if (p) return p.name; }
    return ls.draftProductName.value || prettyRaw(ls.receiptText);
  }

  // ── server-state application (server is authoritative — ADR-020 §2/§7) ────────
  /** @param {LineState} ls @param {any} data */
  function applySaved(ls, data) {
    batch(() => {
      ls.status.value = data.status ?? ls.status.value;
      ls.isNewProduct = data.isNewProduct ?? ls.isNewProduct;
      ls.newProductName = data.newProductName ?? ls.newProductName;
      if (typeof data.price === "number") ls.price.value = data.price;
      if (data.productId) ls.draftProductId.value = data.productId;
      if (data.productName) ls.draftProductName.value = data.productName;
      ls.searchOpen.value = false;
      ls.drawerOpen.value = false;
      ls.error.value = null;
    });
  }

  /**
   * POSTs a lineId-scoped status action (Dismiss/Restore/Reopen) and returns the JSON body or false.
   * @param {string} url @param {LineState} ls @returns {Promise<any|false>}
   */
  async function postAction(url, ls) {
    if (ls.saving.value) return false;
    ls.saving.value = true;
    ls.error.value = null;
    try {
      const resp = await postJson(`${url}&lineId=${ls.lineId}`, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) { ls.error.value = data.error ?? `Action failed (${resp.status})`; return false; }
      return data;
    } catch {
      ls.error.value = "Network error — please try again.";
      return false;
    } finally {
      ls.saving.value = false;
    }
  }
  /** @param {LineState} ls */
  async function postDismiss(ls) { const d = await postAction(hydration.dismissLineUrl, ls); if (!d) return false; batch(() => { ls.status.value = "Dismissed"; ls.drawerOpen.value = false; ls.error.value = null; }); return true; }
  /** @param {LineState} ls */
  async function postRestore(ls) { const d = await postAction(hydration.restoreLineUrl, ls); if (!d) return false; batch(() => { ls.status.value = d.status ?? "Pending"; ls.demoted.value = false; ls.error.value = null; }); return true; }
  /** @param {LineState} ls */
  async function postReopen(ls) { const d = await postAction(hydration.reopenLineUrl, ls); if (!d) return false; batch(() => { ls.status.value = d.status ?? "Pending"; ls.error.value = null; }); return true; }

  // ── client-side validation mirror (server re-validates and is authoritative) ──
  /** @param {LineState} ls @returns {string|null} */
  function validateLine(ls) {
    const qty = parseFloat(ls.draftQty.value);
    if (ls.createNew.value) {
      if (!ls.draftNewName.value.trim() || !ls.draftNewCategoryId.value) return "A new product needs a name and a category.";
    } else if (!ls.draftProductId.value) {
      return "Choose a product, or switch to creating a new one.";
    }
    if (!qty || qty <= 0) return "Enter a quantity greater than zero.";
    if (!ls.draftUnitId.value) return "Choose a unit.";
    if (!ls.draftLocationId.value) return "Choose a location.";
    return null;
  }

  /** SaveLine submit shared by deck confirm and a confirmed-row edit save. @param {LineState} ls @param {(ls: LineState) => void} onOk */
  async function submitSave(ls, onOk) {
    if (ls.saving.value) return;
    const err = validateLine(ls);
    if (err) { ls.error.value = err; return; }
    ls.saving.value = true;
    ls.error.value = null;
    try {
      const resp = await postJson(hydration.saveLineUrl, buildSaveLineBody(ls), token);
      const data = await resp.json();
      if (!resp.ok || data.error) { ls.error.value = data.error ?? `Save failed (${resp.status})`; return; }
      applySaved(ls, data);
      onOk(ls);
    } catch {
      ls.error.value = "Network error — please try again.";
    } finally {
      ls.saving.value = false;
    }
  }

  // ── deck verbs ───────────────────────────────────────────────────────────────
  /** @param {LineState} ls */
  async function deckConfirm(ls) {
    await submitSave(ls, (l) => {
      showToast(`${displayName(l)} confirmed`, () => undoResolve(l));
      syncDeck();
    });
  }
  /** @param {LineState} ls */
  async function deckReject(ls) {
    if (ls.saving.value) return;
    const name = displayName(ls);
    if (await postDismiss(ls)) { showToast(`${name} rejected — won't be added`, () => undoReject(ls)); syncDeck(); }
  }
  function deckSkip() {
    if (order.value.length < 2) return;
    const r = applySkip(order.value, skipStack.value);
    batch(() => { order.value = r.order; skipStack.value = r.skipStack; });
  }
  function deckBack() {
    const r = applyBack(order.value, skipStack.value);
    batch(() => { order.value = r.order; skipStack.value = r.skipStack; });
  }

  // Bind the module-scope helpers the components call (search + create mode).
  enterCreate = (ls) => { batch(() => { ls.createNew.value = true; ls.searchOpen.value = false; }); };
  /** @param {LineState} ls */
  function searchOn(ls) { batch(() => { ls.searchOpen.value = true; ls.createNew.value = false; }); focusSearchInput(ls.lineId); }
  /** @param {LineState} ls */
  function searchOff(ls) { ls.searchOpen.value = false; }
  pickSearchResult = (ls, product) => {
    batch(() => {
      ls.draftProductId.value = product.id;
      ls.draftProductName.value = product.name;
      ls.draftSkuId.value = "";
      ls.searchOpen.value = false;
      if (!ls.draftUnitId.value) ls.draftUnitId.value = product.defaults.unitId;
      if (!ls.draftLocationId.value && product.defaults.locationId) ls.draftLocationId.value = product.defaults.locationId;
      if (ls.draftExpiryMode.value === "never" && product.defaults.expiry) { ls.draftExpiry.value = product.defaults.expiry; ls.draftExpiryMode.value = "has"; }
    });
  };
  /** @param {string} lineId */
  function focusSearchInput(lineId) {
    setTimeout(() => { const el = /** @type {HTMLInputElement|null} */ (document.getElementById(`fc-search-${lineId}`)); if (el) { el.focus(); el.setSelectionRange(el.value.length, el.value.length); } }, 0);
  }

  // ── checklist verbs ──────────────────────────────────────────────────────────
  /** @param {LineState} ls */
  function toggleCheck(ls) { ls.checked.value = !ls.checked.value; }

  /** Bulk-confirm the CHECKED sure things (ConfirmLines); demote the unchecked ones into the deck. */
  async function bulkConfirm() {
    const sure = linesSignal.value.filter((l) => isSurePending(l));
    if (sure.length === 0) return;
    const picked = sure.filter((l) => l.checked.value);
    const demoted = sure.filter((l) => !l.checked.value);

    if (picked.length > 0) {
      picked.forEach((l) => { l.saving.value = true; l.error.value = null; });
      try {
        const resp = await postJson(hydration.confirmLinesUrl, { lineIds: picked.map((l) => l.lineId) }, token);
        const data = await resp.json();
        if (!resp.ok || data.error) { alertMsg.value = data.error ?? `Confirm failed (${resp.status})`; picked.forEach((l) => { l.saving.value = false; }); return; }
        batch(() => { picked.forEach((l) => { l.status.value = "Confirmed"; l.saving.value = false; l.error.value = null; }); });
      } catch {
        alertMsg.value = "Network error — please try again.";
        picked.forEach((l) => { l.saving.value = false; });
        return;
      }
    }

    // Unchecked sure things demote into the deck with a "double-check the match" decision (client-only).
    batch(() => { demoted.forEach((l) => { l.demoted.value = true; l.checked.value = true; }); });
    syncDeck();

    const confirmedIds = picked.map((l) => l.lineId);
    const label = `${picked.length} sure ${picked.length === 1 ? "thing" : "things"} confirmed` +
      (demoted.length ? ` · ${demoted.length} sent to judgement` : "");
    showToast(label, confirmedIds.length ? () => undoBulk(confirmedIds) : null);
  }

  // ── confirmed-row verbs ──────────────────────────────────────────────────────
  /** @param {LineState} ls */
  async function saveEdit(ls) { await submitSave(ls, (l) => showToast(`${displayName(l)} updated`, null)); }
  /** "Wrong product — review again": reopen a confirmed line and push it to the deck top. @param {LineState} ls */
  async function rematch(ls) {
    if (ls.saving.value) return;
    if (ls.status.value === "Confirmed") { if (!(await postReopen(ls))) return; }
    batch(() => { ls.draftProductId.value = ""; ls.draftSkuId.value = ""; ls.createNew.value = false; ls.demoted.value = false; ls.drawerOpen.value = false; });
    syncDeck();
    moveToDeckTop(ls.lineId);
  }
  /** @param {LineState} ls */
  async function rowReject(ls) {
    if (ls.saving.value) return;
    const name = displayName(ls);
    if (await postDismiss(ls)) { showToast(`${name} removed — won't be added`, () => undoReject(ls)); syncDeck(); }
  }
  /** "Add anyway" on a skipped row. @param {LineState} ls */
  async function restore(ls) {
    if (ls.saving.value) return;
    const name = displayName(ls);
    if (await postRestore(ls)) { showToast(`${name} added back`, () => undoRestore(ls)); syncDeck(); if (lineSection(ls) === "needs") moveToDeckTop(ls.lineId); }
  }

  // ── undo closures (each maps to the inverse server endpoint) ─────────────────
  /** @param {LineState} ls */
  async function undoResolve(ls) { if (await postReopen(ls)) { syncDeck(); moveToDeckTop(ls.lineId); } }
  /** @param {LineState} ls */
  async function undoReject(ls) { if (await postRestore(ls)) { syncDeck(); if (lineSection(ls) === "needs") moveToDeckTop(ls.lineId); } }
  /** @param {LineState} ls */
  async function undoRestore(ls) { await postDismiss(ls); syncDeck(); }
  /** @param {string[]} ids */
  async function undoBulk(ids) {
    for (const id of ids) { const l = byId(id); if (l) await postReopen(l); }
    syncDeck();
  }

  // ── minimap / navigation ─────────────────────────────────────────────────────
  /** @param {string} lineId */
  function moveToDeckTop(lineId) {
    const rest = order.value.filter((id) => id !== lineId);
    order.value = [lineId, ...rest];
    setTimeout(() => { document.querySelector(".focus-card")?.scrollIntoView({ behavior: "smooth", block: "center" }); }, 0);
  }
  /** @param {string} lineId */
  function railJump(lineId) {
    const ls = byId(lineId);
    if (!ls) return;
    const section = lineSection(ls);
    if (section === "needs") { syncDeck(); moveToDeckTop(lineId); }
    else if (section === "confirmed") { if (ls.status.value !== "Committed") ls.drawerOpen.value = true; setTimeout(() => { document.querySelector(".import-row--confirmed.import-row--open")?.scrollIntoView({ behavior: "smooth", block: "center" }); }, 0); }
    else if (section === "sure") {
      setTimeout(() => {
        const row = document.querySelector(`.check-row[data-line-id="${lineId}"]`);
        if (row) { row.scrollIntoView({ behavior: "smooth", block: "center" }); row.classList.add("flash"); setTimeout(() => row.classList.remove("flash"), 1200); }
      }, 0);
    }
  }

  // ── commit / discard ─────────────────────────────────────────────────────────
  async function commit() {
    try {
      const resp = await postJson(hydration.commitUrl, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) { alertMsg.value = data.error ?? `Commit failed (${resp.status})`; return; }
      if (data.redirectUrl) window.location.href = data.redirectUrl;
    } catch { alertMsg.value = "Network error — please try again."; }
  }
  async function discard() {
    try {
      const resp = await postJson(hydration.discardUrl, {}, token);
      const data = await resp.json();
      if (!resp.ok || data.error) { alertMsg.value = data.error ?? `Discard failed (${resp.status})`; return; }
      if (data.redirectUrl) window.location.href = data.redirectUrl;
    } catch { alertMsg.value = "Network error — please try again."; }
  }

  // ── keyboard — the deals-deck vocabulary (Enter/→ confirm, X/← reject, M, N, S, Z) + intake's U ──
  document.addEventListener("keydown", (e) => {
    const target = /** @type {HTMLElement} */ (e.target);
    const inField = !!(target.closest && target.closest("input, textarea, select, [contenteditable]"));

    // Enter inside a deck-card field confirms (per prototype) — but never inside the search combobox,
    // which owns Enter for its own listbox, and never inside another row's own form.
    if (inField) {
      if (e.key === "Enter" && target.getAttribute("role") !== "combobox" && target.closest(".focus-card")) {
        const top = byId(order.value.filter((id) => { const l = byId(id); return l && lineSection(l) === "needs"; })[0]);
        if (top) { e.preventDefault(); deckConfirm(top); }
      }
      return;
    }
    if (target.closest && target.closest("button, a")) return; // never hijack a focused control

    if (e.key === "u" || e.key === "U") { e.preventDefault(); doUndo(); return; }

    const top = byId(order.value.filter((id) => { const l = byId(id); return l && lineSection(l) === "needs"; })[0]);
    if (!top) return;

    const verb =
      e.key === "Enter" || e.key === "ArrowRight" ? "confirm"
        : e.key === "ArrowLeft" || e.key === "x" || e.key === "X" ? "reject"
          : e.key === "m" || e.key === "M" ? "match"
            : e.key === "n" || e.key === "N" ? "create"
              : e.key === "s" || e.key === "S" ? "skip"
                : e.key === "z" || e.key === "Z" ? "back"
                  : /^[1-9]$/.test(e.key) ? "pick"
                    : null;
    if (!verb) return;
    e.preventDefault();
    switch (verb) {
      case "confirm": deckConfirm(top); break;
      case "reject": deckReject(top); break;
      case "match": searchOn(top); break;
      case "create": enterCreate(top); break;
      case "skip": deckSkip(); break;
      case "back": deckBack(); break;
      case "pick": if (!top.searchOpen.value && !top.createNew.value) pickAlternative(top, Number(e.key) - 1); break;
    }
  });

  // ── receipt-header correction verbs (plantry-yobz) ───────────────────────────
  /**
   * Persist the whole header (store + date + time) via CorrectHeader, then re-lock from the server echo.
   * @param {{ merchantText: string, selectedStoreId: string, purchaseDate: string, purchaseTime: string }} next
   */
  async function saveHeader(next) {
    if (header.saving.value) return false;
    header.saving.value = true;
    header.error.value = "";
    try {
      const resp = await postJson(hydration.correctHeaderUrl, buildCorrectHeaderBody(next), token);
      const data = await resp.json();
      if (!resp.ok || data.error) { header.error.value = data.error ?? `Save failed (${resp.status})`; return false; }
      batch(() => {
        header.merchantText.value = data.merchantText ?? "";
        header.selectedStoreId.value = data.selectedStoreId ?? "";
        header.purchaseDateDisplay.value = data.purchaseDate ?? "";
        header.purchaseTimeDisplay.value = data.purchaseTime ?? "";
        header.purchaseDateRaw.value = data.purchaseDateRaw ?? "";
        header.purchaseTimeRaw.value = data.purchaseTimeRaw ?? "";
        header.storeEditing.value = false;
        header.dateEditing.value = false;
        header.error.value = "";
      });
      return true;
    } catch {
      header.error.value = "Network error — please try again.";
      return false;
    } finally {
      header.saving.value = false;
    }
  }
  function headerEditStore() {
    batch(() => { header.draftMerchant.value = header.merchantText.value; header.error.value = ""; header.storeEditing.value = true; header.dateEditing.value = false; });
    setTimeout(() => { const el = /** @type {HTMLInputElement|null} */ (document.getElementById("rcpt-store-search")); if (el) el.focus(); }, 0);
  }
  function headerCancelStore() { batch(() => { header.storeEditing.value = false; header.error.value = ""; }); }
  /** @param {StoreHydration} store */
  function headerPickStore(store) {
    saveHeader({ merchantText: store.name, selectedStoreId: store.id, purchaseDate: header.purchaseDateRaw.value, purchaseTime: header.purchaseTimeRaw.value });
  }
  function headerCreateStore() {
    const name = header.draftMerchant.value.trim();
    if (!name) { header.error.value = "Enter a store name."; return; }
    // A typed name drops any prior store pick — commit resolves it via find-or-create (create-new path).
    saveHeader({ merchantText: name, selectedStoreId: "", purchaseDate: header.purchaseDateRaw.value, purchaseTime: header.purchaseTimeRaw.value });
  }
  function headerEditDate() {
    batch(() => { header.draftDate.value = header.purchaseDateRaw.value; header.draftTime.value = header.purchaseTimeRaw.value; header.error.value = ""; header.dateEditing.value = true; header.storeEditing.value = false; });
  }
  function headerCancelDate() { batch(() => { header.dateEditing.value = false; header.error.value = ""; }); }
  function headerSaveDate() {
    saveHeader({ merchantText: header.merchantText.value, selectedStoreId: header.selectedStoreId.value, purchaseDate: header.draftDate.value, purchaseTime: header.draftTime.value });
  }

  const handlers = {
    deckConfirm, deckReject, deckSkip, deckBack, searchOn, searchOff,
    toggleCheck, bulkConfirm, saveEdit, rematch, rowReject, restore,
    railJump, undo: doUndo, dismissToast: hideToast, commit, discard,
    headerEditStore, headerCancelStore, headerPickStore, headerCreateStore,
    headerEditDate, headerCancelDate, headerSaveDate,
  };

  // E2E / test seam
  window.__intakeReviewIsland = {
    /** @returns {number} */ needsCount() { return linesSignal.value.filter((l) => lineSection(l) === "needs").length; },
    /** @returns {number} */ sureCount() { return linesSignal.value.filter((l) => lineSection(l) === "sure").length; },
    /** @returns {number} */ confirmedCount() { return linesSignal.value.filter((l) => lineSection(l) === "confirmed").length; },
    /** @returns {string|null} */ topCardId() { const ids = linesSignal.value.filter((l) => lineSection(l) === "needs").map((l) => l.lineId); return order.value.find((id) => ids.includes(id)) ?? null; },
  };

  render(
    html`<${App}
      lines=${linesSignal} order=${order} skipStack=${skipStack} baseline=${baseline}
      products=${hydration.products} units=${hydration.units} locations=${hydration.locations} categories=${hydration.categories}
      session=${hydration} header=${header} alert=${alertMsg} toastMsg=${toastMsg} toastUndo=${toastUndo}
      handlers=${handlers} />`,
    root,
  );
}
