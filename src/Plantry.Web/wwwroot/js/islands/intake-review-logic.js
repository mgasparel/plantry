// @ts-check
//
// intake-review-logic.js — pure transforms for the Intake Review island (ADR-020, bead plantry-2zvm.11).
//
// The Intake review surface follows the DEALS-DECK flow (epic plantry-wmgg / prototype
// .preview/intake-review-deck.html): one screen with four pools — a "judgement calls" deck of
// exceptions, a "sure things" checklist, a confirmed list, and a skipped list. The deck's order /
// skip-stack / swipe-geometry primitives are the SAME ones the Deals judgement deck uses, so this
// module re-uses (never re-implements) the already-extracted, tested transforms from
// deal-deck-logic.js (Gate 6 reuse discipline). Intake-specific transforms — four-way sectioning
// with the ConfirmLines-mirroring "sure" predicate, uncheck-demote decision synthesis, rail glyph
// state, and money reconciliation — live here.
//
// CONVENTION (island testing):
//   Pure transforms are extracted into a sibling `*-logic.js` module.
//   The island (`intake-review.js`) imports and calls them.
//   Tests (`__tests__/intake-review-logic.test.js`) import from here using
//   `node --test` (built-in, zero deps).
//   This keeps the island file focused on wiring/rendering; the running file
//   is still the file you read (no build, no transpile).
//
// What belongs here (ADR-020 §2 / §7 boundary):
//   UI/draft state transforms — hydration→signal factories, draft→POST-body
//   builders, deck presentation order, and display-value helpers. These are pure
//   functions of their arguments and hold NO domain logic. They do not compute
//   fulfillment, cost, validation-as-truth, or any catalog/unit-semantics rule.
//
// What does NOT belong here:
//   Anything that crosses the ADR-020 §7 tripwire. If you need domain rules,
//   call a server endpoint instead. The deck verbs still round-trip through the
//   JSON endpoints (SaveLine / DismissLine / ConfirmLines / Reopen / Restore);
//   this module only owns order, drag geometry, sectioning, and display.

// ── Reused deck primitives (Gate 6: extract-before-repeat — deal-deck-logic already owns these) ──
//
// Deck membership/order (rebuilt from truth while preserving skip-rotation order), skip/back
// rotation, skip-stack reconciliation, swipe geometry, and the high-water progress baseline are
// identical to the Deals judgement deck. Import + re-export them so the island imports from one
// place and the intake test suite covers intake's use of them (acceptance #1: "deck order +
// skip-stack rotation"). The ?v= token busts this transitive import's browser cache (plantry-hxkf).
export {
  buildDeckOrder,
  rotateToEnd,
  applySkip,
  applyBack,
  reconcileSkipStack,
  swipeVerb,
  stampOpacity,
  cardTransform,
  nextBaseline,
  deckProgress,
  DECK_SWIPE_THRESHOLD,
} from "./deal-deck-logic.js?v=1";

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
 * @typedef {Object} AlternativeHydration
 * @property {string} productId
 * @property {string} productName
 * @property {number} confidence  0..1 fraction
 */

/**
 * @typedef {Object} EstimateHydration
 * @property {number} eachCount
 * @property {number} weight
 * @property {string} weightUnit
 * @property {string} confidence   "High" | "Low"
 */

/**
 * Minimal signal shape used by tests (mirrors the @preact/signals API surface
 * that the logic functions depend on). The island passes real signals; the test
 * rig passes these plain objects.
 *
 * @template T
 * @typedef {{ value: T }} SignalLike
 */

/**
 * @typedef {Object} LineState
 * @property {string} lineId
 * @property {string} receiptText
 * @property {string} confidence
 * @property {boolean} aiComplete   the ORIGINAL server-hydrated AI prefill was already complete (product + qty>0 + unit + location) — an immutable snapshot taken at hydration, NOT a live read of the (user-editable) draft signals. Gates the "sure" checklist so a user edit that completes a line never promotes it (plantry-wv4h).
 * @property {SignalLike<string>} status
 * @property {boolean} isNewProduct
 * @property {string|null} newProductName
 * @property {SignalLike<number|null>} price
 * @property {SignalLike<boolean>} saving
 * @property {SignalLike<string|null>} error
 * @property {SignalLike<boolean>} drawerOpen
 * @property {SignalLike<boolean>} searchOpen
 * @property {SignalLike<boolean>} createNew
 * @property {SignalLike<boolean>} checked   sure-things checkbox (pre-checked; meaningful only in the "sure" section)
 * @property {SignalLike<boolean>} demoted   a sure line the user pushed into the deck (client-only override)
 * @property {SignalLike<string>} draftProductId
 * @property {SignalLike<string>} draftProductName
 * @property {SignalLike<string>} draftSkuId
 * @property {SignalLike<string>} draftQty
 * @property {SignalLike<string>} draftUnitId
 * @property {SignalLike<string>} draftLocationId
 * @property {SignalLike<string>} draftExpiry
 * @property {SignalLike<string>} draftExpiryMode
 * @property {SignalLike<string>} draftPrice
 * @property {SignalLike<string>} draftNewName
 * @property {SignalLike<string>} draftNewCategoryId
 * @property {AlternativeHydration[]|null} alternatives
 * @property {EstimateHydration|null} estimate
 */

// ── makeLine ─────────────────────────────────────────────────────────────────

/**
 * Build initial LineState from server hydration seed.
 *
 * Pure function of its arguments — signal() is injected so tests can pass a
 * plain-object factory rather than real Preact signals.
 *
 * `checked` starts true so a sure-things line is pre-checked in the checklist (deck flow); `demoted`
 * starts false — it flips to true only when the user unchecks a sure line and pushes it into the deck.
 * `drawerOpen` governs the confirmed-row inline edit drawer only (it starts closed; the user toggles
 * it). The deck's top card is order[0] in the island, never a per-line flag.
 *
 * @template {SignalLike<any>} S
 * @param {{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null, estimate?: EstimateHydration|null}} seed
 * @param {(v: any) => S} signalFn  — real `signal` from Preact or a plain-object stub for tests
 * @returns {LineState}
 */
export function makeLine(seed, signalFn) {
  const { line, prefill, alternatives, estimate } = seed;
  const effectivePrice = line.price ?? line.suggestedPrice;
  return {
    lineId: line.lineId,
    receiptText: line.receiptText,
    confidence: line.confidence,
    // Snapshot whether the AI's ORIGINAL suggestion was already complete — the same values ConfirmLines
    // re-derives server-side. Frozen here so later user edits to the draft signals can't flip it (plantry-wv4h).
    aiComplete: isPrefillComplete(prefill.productId, prefill.quantity, prefill.unitId, prefill.locationId),
    status: signalFn(line.status),
    isNewProduct: line.isNewProduct,
    newProductName: line.newProductName,
    price: signalFn(typeof effectivePrice === "number" ? effectivePrice : null),
    saving: signalFn(false),
    error: signalFn(/** @type {string|null} */ (null)),
    drawerOpen: signalFn(false),
    searchOpen: signalFn(false),
    createNew: signalFn(line.isNewProduct),
    checked: signalFn(true),
    demoted: signalFn(false),
    draftProductId: signalFn(prefill.productId ?? ""),
    draftProductName: signalFn(prefill.productName ?? ""),
    draftSkuId: signalFn(prefill.skuId ?? ""),
    draftQty: signalFn(prefill.quantity != null ? String(prefill.quantity) : ""),
    draftUnitId: signalFn(prefill.unitId ?? ""),
    draftLocationId: signalFn(prefill.locationId ?? ""),
    draftExpiry: signalFn(prefill.expiry ?? ""),
    draftExpiryMode: signalFn(prefill.expiry ? "has" : "never"),
    draftPrice: signalFn(prefill.price != null ? String(prefill.price) : ""),
    draftNewName: signalFn(line.newProductName ?? ""),
    draftNewCategoryId: signalFn(line.newProductCategoryId ?? ""),
    alternatives: alternatives ?? null,
    estimate: estimate ?? null,
  };
}

// ── estimateHint ───────────────────────────────────────────────────────────────

/**
 * Human-readable weight→each affordance for the card (plantry-1mu). Returns null when the line
 * carries no estimate. Pure display formatting — no domain decision (the prefill choice is server-side).
 *
 * @param {EstimateHydration|null} estimate
 * @returns {string|null}
 */
export function estimateHint(estimate) {
  if (!estimate) return null;
  const count = `~${estimate.eachCount} each`;
  const weight = `${estimate.weight} ${estimate.weightUnit}`;
  return estimate.confidence === "High"
    ? `${count} · estimated from ${weight}`
    : `Sold by weight (${weight}) · ${count}?`;
}

// ── "sure" predicate (client mirror of the server's ConfirmLines qualification) ──

/**
 * The core "complete prefill" predicate over four raw values — an existing product, a quantity > 0, a
 * unit, and a location. This is the ONE place the four-field rule lives; both the live-draft check
 * ({@link hasCompletePrefill}) and the frozen AI snapshot ({@link makeLine}'s `aiComplete`) go through it.
 *
 * SINGLE SOURCE OF TRUTH — this MUST stay identical to the server predicate in
 * {@link "../../../Plantry.Intake/Application/ConfirmLinesCommand.cs"} ConfirmLinesCommand.ExecuteAsync
 * (plantry-kr9h), which since this rewrite holds the ONE authoritative qualification predicate (the
 * old CommitSessionCommand commit-time auto-confirm pre-pass that used to duplicate it is deleted):
 *   prefill.ProductId is not null && prefill.Qty is > 0m
 *   && prefill.UnitId is not null && prefill.LocationId is not null
 * If the two drift, the checklist and the server will disagree about which High-confidence Pending
 * lines can be bulk-confirmed.
 *
 * @param {string|null|undefined} productId
 * @param {number|null|undefined} quantity
 * @param {string|null|undefined} unitId
 * @param {string|null|undefined} locationId
 * @returns {boolean}
 */
export function isPrefillComplete(productId, quantity, unitId, locationId) {
  return !!productId && typeof quantity === "number" && quantity > 0 && !!unitId && !!locationId;
}

/**
 * Whether the line's CURRENT draft signals form a complete prefill — the live, user-editable view of
 * {@link isPrefillComplete}. Used where "can this line be saved/confirmed right now" is the question
 * (e.g. deck-card validation). It is NOT what qualifies a line for the bulk-confirm checklist — that is
 * {@link isSurePending}, which reads the frozen AI snapshot so a user edit can never promote a line.
 *
 * @param {LineState} ls
 * @returns {boolean}
 */
export function hasCompletePrefill(ls) {
  return isPrefillComplete(
    ls.draftProductId.value,
    parseFloat(ls.draftQty.value),
    ls.draftUnitId.value,
    ls.draftLocationId.value,
  );
}

/**
 * A "sure thing": a still-Pending line the AI was High-confident about whose ORIGINAL prefill was
 * already complete — the checklist's pre-checked, bulk-confirmable rows. It gates on the immutable
 * `aiComplete` snapshot ({@link makeLine}), NOT the live draft signals: bulk-confirm re-derives the
 * prefill server-side from the untouched AI suggestion (Gate 5), so only a line the AI itself completed
 * can survive it. A High-confidence line the AI left incomplete stays in the editable deck even after
 * the user manually completes its fields (plantry-wv4h) — the user resolves it via SaveLine there.
 *
 * Plus the client-only `demoted` override: a sure line the user unchecked and pushed into the deck is
 * NOT shown as a sure thing (it is a "needs" deck card) even though the server would still accept it —
 * the user asked to double-check it, so it goes to the deck.
 *
 * @param {LineState} ls
 * @returns {boolean}
 */
export function isSurePending(ls) {
  return (
    ls.status.value === "Pending" &&
    ls.confidence === "High" &&
    !ls.demoted.value &&
    ls.aiComplete
  );
}

// ── lineSection ─────────────────────────────────────────────────────────────────

/**
 * Derive which display pool a line belongs to (deals-deck flow):
 *   • "skipped"   — Dismissed.
 *   • "confirmed" — Confirmed / Committed (going to the pantry on commit).
 *   • "sure"      — a Pending, High-confidence, complete-prefill line (the pre-checked checklist).
 *   • "needs"     — every other Pending line, plus any sure line the user demoted (the judgement deck).
 *
 * @param {LineState} ls
 * @returns {"confirmed" | "sure" | "needs" | "skipped"}
 */
export function lineSection(ls) {
  const s = ls.status.value;
  if (s === "Dismissed") return "skipped";
  if (s === "Confirmed" || s === "Committed") return "confirmed";
  return isSurePending(ls) ? "sure" : "needs";
}

// ── deck card classification (which question the exception asks) ─────────────────

/**
 * Classify the kind of decision a deck card asks — all derivable from existing hydration (no new AI
 * data). Priority:
 *   1. "create"   — createNew already set, OR nothing to match to (no product and no alternatives).
 *   2. "estimate" — a weight→each estimate is present → "how should we count this?".
 *   3. "match"    — 2+ resolved alternatives → "which product is this?".
 *   4. "sku"      — the matched product has 2+ pack sizes → "which size did you buy?".
 *   5. "fields"   — a match with only details left to confirm (e.g. a demoted or incomplete line).
 *
 * @param {LineState} ls
 * @param {number} skuCount  — pack-size count for the currently-drafted product (island-derived)
 * @returns {"create" | "estimate" | "match" | "sku" | "fields"}
 */
export function decisionVariant(ls, skuCount = 0) {
  if (ls.createNew.value) return "create";
  const hasProduct = !!ls.draftProductId.value;
  const alts = ls.alternatives;
  if (!hasProduct && (!alts || alts.length === 0)) return "create";
  if (ls.estimate) return "estimate";
  if (alts && alts.length >= 2) return "match";
  if (skuCount >= 2) return "sku";
  return "fields";
}

/**
 * The reasoning line rendered inside the deck card's `.focus-card__match` (a STATEMENT, not the old
 * single-focus question framing — that `.review-q` copy is removed with the canonical flow). Static UI
 * copy, kept here so it is covered by the same node --test rig.
 *
 * @param {"create" | "estimate" | "match" | "sku" | "fields"} variant
 * @returns {string}
 */
export function deckReasoning(variant) {
  switch (variant) {
    case "create":
      return "Nothing in your catalog matches — add it as a new product, match it yourself, or reject it.";
    case "estimate":
      return "It was sold by weight, but your pantry may count it a different way — pick how to count it.";
    case "match":
      return "A few products in your catalog share these words — the top pick is the one you buy most.";
    case "sku":
      return "This product comes in more than one pack size — pick the one you bought.";
    default:
      return "Double-check the match and details below, then add it.";
  }
}

/**
 * The rank badge (`.rk`) label for a candidate in the deck's `.suggest-opts`: the top recommended
 * option reads "best"; the rest read their confidence as a percentage; a zero-confidence option
 * (e.g. a "no specific size" escape) shows nothing. Pure display formatting — mirrors AlternativesStrip.
 *
 * @param {number} index          0-based rank
 * @param {number} confidence     0..1 fraction
 * @param {boolean} isRecommended the option is the recommended top pick
 * @returns {string}
 */
export function optionRankLabel(index, confidence, isRecommended) {
  if (index === 0 && isRecommended) return "best";
  if (confidence > 0) return `${Math.round(confidence * 100)}%`;
  return "";
}

// ── uncheck-demote decision synthesis ────────────────────────────────────────────

/**
 * The synthetic single-option decision shown when a user UNCHECKS a sure thing and it demotes into the
 * deck (deal-deck-flow checklist semantics). The deck card renders one candidate — the line's current
 * match — under a "double-check the match" prompt, so the user re-confirms (or changes / rejects) it
 * deliberately instead of it sliding into the pantry unreviewed. Pure: derives the option purely from
 * the line's already-held product match.
 *
 * @param {string} productName  the current matched product's display name
 * @param {string|null} productId  the current matched product id (null if unresolved)
 * @param {number} confidence   0..1 fraction (defaults to 0 when unknown)
 * @returns {{ reasoning: string, option: { label: string, productId: string|null, confidence: number, recommended: true } }}
 */
export function demotedDecision(productName, productId, confidence = 0) {
  return {
    reasoning: "You unchecked this in the sure things — double-check the match before adding it.",
    option: {
      label: productName,
      productId: productId ?? null,
      confidence,
      recommended: true,
    },
  };
}

// ── buildSaveLineBody ─────────────────────────────────────────────────────────

/**
 * Build the POST body for the SaveLine endpoint from the current LineState.
 *
 * This is the payload that resolves a line — extract + test explicitly so every edge (garbage qty,
 * createNew branch, expiry mode, optional coercions) has a contract.
 *
 * Rules:
 * - `quantity` is always `parseFloat(ls.draftQty.value)` — callers must validate > 0 before calling
 *   this; NaN propagates and the server rejects.
 * - In the `createNew` branch: `productId` and `skuId` are null; `newProductName` is trimmed and
 *   `newProductCategoryId` is the raw value or null.
 * - In the existing-product branch: `newProductName` and `newProductCategoryId` are null; `productId`
 *   / `skuId` use `(value || null)` so empty strings become null.
 * - `expiryDate` is non-null only when `draftExpiryMode === "has"` AND `draftExpiry` is non-empty.
 * - `price` is `parseFloat(draftPrice)` when non-empty; otherwise null.
 * - `unitId` and `locationId` use `(value || null)`.
 *
 * @param {LineState} ls
 * @returns {{
 *   lineId: string, createNew: boolean, productId: string|null, skuId: string|null,
 *   newProductName: string|null, newProductCategoryId: string|null, quantity: number,
 *   unitId: string|null, locationId: string|null, expiryDate: string|null, price: number|null,
 * }}
 */
export function buildSaveLineBody(ls) {
  return {
    lineId: ls.lineId,
    createNew: ls.createNew.value,
    productId: ls.createNew.value ? null : (ls.draftProductId.value || null),
    skuId: ls.createNew.value ? null : (ls.draftSkuId.value || null),
    newProductName: ls.createNew.value ? ls.draftNewName.value.trim() : null,
    newProductCategoryId: ls.createNew.value ? (ls.draftNewCategoryId.value || null) : null,
    quantity: parseFloat(ls.draftQty.value),
    unitId: ls.draftUnitId.value || null,
    locationId: ls.draftLocationId.value || null,
    expiryDate: ls.draftExpiryMode.value === "has" && ls.draftExpiry.value ? ls.draftExpiry.value : null,
    price: ls.draftPrice.value ? parseFloat(ls.draftPrice.value) : null,
  };
}

// ── review header correction (plantry-yobz) ──────────────────────────────────────

/**
 * Filter the household's active stores by a free-text query (case-insensitive substring), capped at
 * `limit` hits — the review header's store picker, mirroring MatchBlock's product filter. Pure display
 * transform: an empty/blank query returns the first `limit` stores. No domain rule (the server validates
 * the eventual pick).
 *
 * @param {{id: string, name: string}[]} stores
 * @param {string} query
 * @param {number} [limit]
 * @returns {{id: string, name: string}[]}
 */
export function filterStores(stores, query, limit = 6) {
  const q = (query ?? "").trim().toLowerCase();
  const hits = q ? stores.filter((s) => s.name.toLowerCase().includes(q)) : stores;
  return hits.slice(0, limit);
}

/**
 * Build the POST body for the CorrectHeader endpoint from the current header draft (plantry-yobz).
 *
 * Rules (all blank→null so the server clears the field rather than storing ""):
 * - `merchantText` is trimmed; empty becomes null.
 * - `selectedStoreId` passes through when non-empty, else null (the merchant-text find-or-create path).
 * - `purchaseDate` / `purchaseTime` are the raw control values (ISO `yyyy-MM-dd` / 24h `HH:mm`); empty → null.
 *
 * Takes plain values (not signals) so it is unit-testable without a signal rig; the island reads the
 * signal `.value`s and passes them in.
 *
 * @param {{ merchantText: string, selectedStoreId: string, purchaseDate: string, purchaseTime: string }} draft
 * @returns {{ merchantText: string|null, selectedStoreId: string|null, purchaseDate: string|null, purchaseTime: string|null }}
 */
export function buildCorrectHeaderBody(draft) {
  return {
    merchantText: (draft.merchantText ?? "").trim() || null,
    selectedStoreId: draft.selectedStoreId || null,
    purchaseDate: draft.purchaseDate || null,
    purchaseTime: draft.purchaseTime || null,
  };
}

// ── commit-bar arithmetic ───────────────────────────────────────────────────────

/**
 * Pure commit-bar arithmetic over an array of line sections. Single source of truth for the commit
 * gate and the counts it shows: `remaining` (the bar's "N to resolve") and `canCommit` both derive
 * from the still-unresolved pools (sure + needs), so the displayed count and the disabled-button state
 * can never disagree. This matches the reverted server contract — CommitSessionCommand now blocks on
 * ANY still-Pending line (both the "sure" and "needs" pools are Pending), so commit is enabled only
 * once every sure thing has been bulk-confirmed and every deck card resolved.
 *
 * `progressPct` mirrors the receipt prototype's meter: (confirmed + skipped) / all-lines — how much of
 * the receipt is dealt with, including skipped fees.
 *
 * @param {("confirmed"|"sure"|"needs"|"skipped")[]} sections — lineSection(ls) for each line
 * @returns {{ confirmedCount: number, sureCount: number, needsCount: number, skippedCount: number,
 *             unresolved: number, totalItems: number, canCommit: boolean, remaining: number, progressPct: number }}
 */
export function commitBarCounts(sections) {
  const confirmedCount = sections.filter((s) => s === "confirmed").length;
  const sureCount = sections.filter((s) => s === "sure").length;
  const needsCount = sections.filter((s) => s === "needs").length;
  const skippedCount = sections.filter((s) => s === "skipped").length;
  const unresolved = sureCount + needsCount;
  const totalItems = confirmedCount + unresolved; // destined for the pantry (skipped excluded)
  const allLines = totalItems + skippedCount;
  const done = confirmedCount + skippedCount;
  return {
    confirmedCount,
    sureCount,
    needsCount,
    skippedCount,
    unresolved,
    totalItems,
    canCommit: unresolved === 0 && confirmedCount > 0,
    remaining: unresolved,
    progressPct: allLines > 0 ? Math.round((done / allLines) * 100) : 100,
  };
}

// ── receipt minimap: per-line glyph state ────────────────────────────────────────

/**
 * Per-line view state for the receipt minimap (the navigable facsimile rail). Pure function of the
 * line's display section + whether it currently holds the deck's active (top-card) slot. Derives from
 * the SAME {@link lineSection} the review list uses, so the rail and the list never disagree.
 *
 *   • "confirmed" → `done: true` (the name dims via .rcpt-line--done) + a "tick" glyph.
 *   • "needs"     → a pulsing "dot" glyph (an open judgement call).
 *   • "sure"      → no glyph, not dimmed (an unconfirmed sure thing).
 *   • "skipped"   → `dim: true` (composes the existing .dim modifier) + no glyph.
 * The active line (the deck's top card) additionally gets `active: true` (.rcpt-line--active highlight).
 *
 * @param {"confirmed" | "sure" | "needs" | "skipped"} section — lineSection(ls)
 * @param {boolean} isActive — the line is the deck's top card
 * @returns {{ done: boolean, dim: boolean, active: boolean, glyph: "tick" | "dot" | null }}
 */
export function railLineView(section, isActive) {
  return {
    done: section === "confirmed",
    dim: section === "skipped",
    active: !!isActive,
    glyph: section === "confirmed" ? "tick" : section === "needs" ? "dot" : null,
  };
}

// ── receipt minimap: money reconciliation ────────────────────────────────────────

/** Round to cents, guarding against float drift (e.g. 0.1 + 0.2). @param {number} n */
function round2(n) {
  return Math.round((n + Number.EPSILON) * 100) / 100;
}

/**
 * Money reconciliation for the receipt footer. PURE DISPLAY ARITHMETIC — it partitions the line prices
 * by their display section and pairs them with the receipt's own tax/total from hydration. It crosses
 * no ADR-020 §7 boundary: it computes no domain rule, only sums numbers the UI already holds.
 *
 * Buckets (deal-deck flow): `pantry` = the confirmed pool (going to the pantry on commit); `undecided`
 * = everything still to resolve (sure + needs); `skippedFees` = the skipped pool.
 *
 * Footer reads: "$pantry going to pantry · $undecided undecided · $skippedFees fees skipped · $tax tax
 * = $total receipt total". The island omits the undecided / fees / tax segments when zero or absent.
 *
 * GRACEFUL DEGRADATION: hydration tax/total are nullable. A missing (or non-finite) tax/total becomes
 * `null` here — never NaN — so the island drops the segment rather than render "$NaN". The
 * pantry/undecided/fees sums are always finite (a null line price counts as 0).
 *
 * `reconciles` is true only when a total is present AND the parts add up to it within a cent — it
 * drives the "✓" confirmation, so a torn receipt (parts ≠ total) shows the breakdown without the tick.
 *
 * @param {Array<{ section: "confirmed" | "sure" | "needs" | "skipped", price: number|null }>} items
 * @param {number|null|undefined} tax   — hydration receipt tax (nullable)
 * @param {number|null|undefined} total — hydration receipt total (nullable)
 * @returns {{ pantry: number, undecided: number, skippedFees: number,
 *             tax: number|null, total: number|null, reconciles: boolean }}
 */
export function reconciliation(items, tax, total) {
  const sumWhere = (/** @type {(section: string) => boolean} */ pred) =>
    round2(items.filter((i) => pred(i.section)).reduce((s, i) => s + (i.price ?? 0), 0));
  const pantry = sumWhere((s) => s === "confirmed");
  const undecided = sumWhere((s) => s === "sure" || s === "needs");
  const skippedFees = sumWhere((s) => s === "skipped");
  const taxVal = typeof tax === "number" && isFinite(tax) ? tax : null;
  const totalVal = typeof total === "number" && isFinite(total) ? total : null;
  let reconciles = false;
  if (totalVal != null) {
    const computed = pantry + undecided + skippedFees + (taxVal ?? 0);
    reconciles = Math.abs(computed - totalVal) < 0.01;
  }
  return { pantry, undecided, skippedFees, tax: taxVal, total: totalVal, reconciles };
}
