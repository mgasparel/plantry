// @ts-check
//
// intake-review-logic.js — pure transforms for the Intake Review island (ADR-020, bead plantry-2zvm.11).
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
//   builders, and display-value helpers. These are pure functions of their
//   arguments and hold NO domain logic. They do not compute fulfillment, cost,
//   validation-as-truth, or any catalog/unit-semantics rule.
//
// What does NOT belong here:
//   Anything that crosses the ADR-020 §7 tripwire. If you need domain rules,
//   call a server endpoint instead.

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
 * @property {number} confidence
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
 * @property {SignalLike<string>} status
 * @property {boolean} isNewProduct
 * @property {string|null} newProductName
 * @property {SignalLike<number|null>} price
 * @property {SignalLike<boolean>} saving
 * @property {SignalLike<string|null>} error
 * @property {SignalLike<boolean>} drawerOpen
 * @property {SignalLike<boolean>} searchOpen
 * @property {SignalLike<boolean>} createNew
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
 * drawerOpen governs the READY-row inline edit drawer only (it starts closed;
 * the user toggles it). The exceptions-first flow (plantry-15l3) drives the
 * focused exception's question drawer from a single global `focusId` signal in
 * the island, NOT from this per-line flag — exactly one exception is ever open,
 * so a per-line "starts open" predicate would fight that single-focus model.
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
    status: signalFn(line.status),
    isNewProduct: line.isNewProduct,
    newProductName: line.newProductName,
    price: signalFn(typeof effectivePrice === "number" ? effectivePrice : null),
    saving: signalFn(false),
    error: signalFn(/** @type {string|null} */ (null)),
    // Ready-row edit drawer starts closed; the island opens the FOCUSED exception's
    // question drawer via the global focusId signal, not this flag (plantry-15l3).
    drawerOpen: signalFn(false),
    searchOpen: signalFn(false),
    createNew: signalFn(line.isNewProduct),
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
 * Human-readable weight→each affordance for the drawer (plantry-1mu). Returns null when the line
 * carries no estimate. Pure display formatting — no domain decision (the prefill choice is server-side).
 *
 * High confidence → the count is already pre-filled, so we phrase it as a provenance note
 * ("~7 each · estimated from 1.34 lb"). Low confidence → we phrase it as a soft suggestion
 * ("Sold by weight (1.34 lb) · ~7 each?") since the drawer left the weight in place.
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

// ── ready predicate (mirrors the server's commit-time auto-confirm) ─────────────

/**
 * A line's server-side prefill is "complete" — the four fields the commit-time
 * auto-confirm requires are all present: an existing product, a quantity > 0, a
 * unit, and a location. The island reads them off the draft signals (which are
 * seeded from the same server prefill the auto-confirm re-derives), so the
 * displayed section and the server's commit decision agree.
 *
 * SINGLE SOURCE OF TRUTH — this MUST stay identical to the server predicate in
 * CommitSessionCommand.ExecuteAsync (plantry-v0wl):
 *   prefill.ProductId is not null && prefill.Qty is > 0m
 *   && prefill.UnitId is not null && prefill.LocationId is not null
 * If the two drift, the commit gate and the server will disagree about which
 * High-confidence Pending lines are "ready".
 *
 * @param {LineState} ls
 * @returns {boolean}
 */
export function hasCompletePrefill(ls) {
  return (
    !!ls.draftProductId.value &&
    parseFloat(ls.draftQty.value) > 0 &&
    !!ls.draftUnitId.value &&
    !!ls.draftLocationId.value
  );
}

/**
 * A still-Pending line that the server will auto-confirm at commit time: High
 * confidence AND a complete prefill. Such a line is "ready" (no exception, no
 * per-row Confirm button) — the commit-time auto-confirm handles it. Mirror of
 * the server predicate; see {@link hasCompletePrefill}.
 *
 * @param {LineState} ls
 * @returns {boolean}
 */
export function isReadyPending(ls) {
  return ls.status.value === "Pending" && ls.confidence === "High" && hasCompletePrefill(ls);
}

// ── lineSection ───────────────────────────────────────────────────────────────

/**
 * Derive which display section a line belongs to (exceptions-first flow, plantry-15l3):
 *   • "skipped" — Dismissed.
 *   • "ready"   — Confirmed / Committed, OR a Pending line that will be auto-confirmed
 *                 at commit (High confidence + complete prefill; see {@link isReadyPending}).
 *   • "needs"   — every other Pending line (a genuine exception the user must resolve).
 *
 * @param {LineState} ls
 * @returns {"needs" | "ready" | "skipped"}
 */
export function lineSection(ls) {
  const s = ls.status.value;
  if (s === "Dismissed") return "skipped";
  if (s === "Confirmed" || s === "Committed") return "ready";
  return isReadyPending(ls) ? "ready" : "needs";
}

// ── decisionVariant / questionCopy ──────────────────────────────────────────────

/**
 * Classify the kind of question a focused exception asks — all derivable from
 * existing hydration (no new AI data). Priority:
 *   1. "create"   — createNew already set, OR nothing to match to (no product and
 *                   no alternatives) → offer the create-or-link path.
 *   2. "estimate" — a weight→each estimate is present → "how should we count this?".
 *   3. "match"    — 2+ resolved alternatives → "which product is this?".
 *   4. "sku"      — the matched product has 2+ pack sizes → "which size did you buy?".
 *   5. "fields"   — a High-confidence match with only a missing/edited field → just
 *                   confirm the details.
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
 * Static UI copy (a question + a why-line) for each decision variant. UI copy,
 * not a domain decision — kept here so it is covered by the same node --test rig.
 *
 * @param {"create" | "estimate" | "match" | "sku" | "fields"} variant
 * @returns {{ question: string, why: string }}
 */
export function questionCopy(variant) {
  switch (variant) {
    case "create":
      return {
        question: "First time seeing this — add it as a new product?",
        why: "nothing in your catalog matches; the name and category are drafted from the receipt",
      };
    case "estimate":
      return {
        question: "How should we count this?",
        why: "it was sold by weight, but your pantry may track it a different way",
      };
    case "match":
      return {
        question: "Which product is this?",
        why: "a few products in your catalog share these words",
      };
    case "sku":
      return {
        question: "Which size did you buy?",
        why: "this product comes in more than one pack size",
      };
    default:
      return {
        question: "Add this to your pantry?",
        why: "confirm the details below",
      };
  }
}

// ── exception-queue helpers ─────────────────────────────────────────────────────

/**
 * The lineId of the first line still in the "needs" section (the next exception to
 * focus), or null when the queue is empty. Drives single-focus advance after a
 * resolve/skip and the initial focus on mount.
 *
 * @param {LineState[]} lines
 * @returns {string|null}
 */
export function firstNeedsLineId(lines) {
  for (const ls of lines) {
    if (lineSection(ls) === "needs") return ls.lineId;
  }
  return null;
}

// ── isUnmatched ───────────────────────────────────────────────────────────────

/**
 * Returns true for a Pending line whose AI match is not High confidence — used
 * only to pick the row's visual state class (unmatched vs matched left-border).
 * NOT the sectioning predicate: whether a line is an exception is {@link lineSection}
 * (a High-confidence line with an incomplete prefill is still a "needs" exception).
 *
 * @param {LineState} ls
 * @returns {boolean}
 */
export function isUnmatched(ls) {
  return ls.status.value === "Pending" && ls.confidence !== "High";
}

// ── commitBarCounts ───────────────────────────────────────────────────────────

/**
 * Pure commit-bar arithmetic over an array of line sections. Single source of truth for the
 * counts the commit bar shows AND the commit gate: `remaining` (the bar's "N to resolve") and
 * `canCommit` both derive from `needsCount`, so the displayed count and the disabled-button
 * state can never disagree (they previously came from two different definitions of "done").
 *
 * Under the exceptions-first flow (plantry-15l3) `needsCount` counts only genuine exceptions —
 * a High-confidence Pending line with a complete prefill sections as "ready" (it is auto-confirmed
 * at commit), so it never blocks the gate. See {@link lineSection} / {@link isReadyPending}.
 * @param {("needs"|"ready"|"skipped")[]} sections — lineSection(ls) for each line
 * @returns {{ needsCount: number, readyCount: number, skippedCount: number, totalItems: number,
 *             canCommit: boolean, remaining: number, progressPct: number }}
 */
export function commitBarCounts(sections) {
  const needsCount = sections.filter((s) => s === "needs").length;
  const readyCount = sections.filter((s) => s === "ready").length;
  const skippedCount = sections.filter((s) => s === "skipped").length;
  const totalItems = needsCount + readyCount;
  return {
    needsCount,
    readyCount,
    skippedCount,
    totalItems,
    canCommit: needsCount === 0 && totalItems > 0,
    remaining: needsCount,
    progressPct: totalItems > 0 ? Math.round((readyCount / totalItems) * 100) : 100,
  };
}

// ── buildSaveLineBody ─────────────────────────────────────────────────────────

/**
 * Build the POST body for the SaveLine endpoint from the current LineState.
 *
 * This is the payload that mutates inventory — extract + test explicitly so
 * every edge (garbage qty, createNew branch, expiry mode, optional coercions)
 * has a contract.
 *
 * Rules:
 * - `quantity` is always `parseFloat(ls.draftQty.value)` — callers must
 *   validate > 0 before calling this; NaN propagates and the server rejects.
 * - In the `createNew` branch: `productId` and `skuId` are null; `newProductName`
 *   is trimmed and `newProductCategoryId` is the raw value or null.
 * - In the existing-product branch: `newProductName` and `newProductCategoryId`
 *   are null; `productId` / `skuId` use `(value || null)` so empty strings
 *   become null.
 * - `expiryDate` is non-null only when `draftExpiryMode === "has"` AND
 *   `draftExpiry` is non-empty; otherwise null.
 * - `price` is `parseFloat(draftPrice)` when non-empty; otherwise null.
 * - `unitId` and `locationId` use `(value || null)`.
 *
 * @param {LineState} ls
 * @returns {{
 *   lineId: string,
 *   createNew: boolean,
 *   productId: string|null,
 *   skuId: string|null,
 *   newProductName: string|null,
 *   newProductCategoryId: string|null,
 *   quantity: number,
 *   unitId: string|null,
 *   locationId: string|null,
 *   expiryDate: string|null,
 *   price: number|null,
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
