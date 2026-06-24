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
 * @typedef {Object} AlternativeHydration
 * @property {string} productId
 * @property {string} productName
 * @property {number} confidence
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
 * @property {SignalLike<number|null>} suggestedPrice
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
 */

// ── makeLine ─────────────────────────────────────────────────────────────────

/**
 * Build initial LineState from server hydration seed.
 *
 * Pure function of its arguments — signal() is injected so tests can pass a
 * plain-object factory rather than real Preact signals.
 *
 * The drawerOpen predicate: a line starts open (needing attention) only when
 * all four conditions hold simultaneously:
 *   1. status === "Pending"   — the line has not been actioned
 *   2. confidence !== "High"  — the match is uncertain
 *   3. productId === null     — no product has been associated yet
 *   4. !isNewProduct          — it is not already flagged as a new product
 * A High-confidence matched line starts closed (user can open it to adjust).
 *
 * @template {SignalLike<any>} S
 * @param {{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null}} seed
 * @param {(v: any) => S} signalFn  — real `signal` from Preact or a plain-object stub for tests
 * @returns {LineState}
 */
export function makeLine(seed, signalFn) {
  const { line, prefill, alternatives } = seed;
  const effectivePrice = line.price ?? line.suggestedPrice;
  return {
    lineId: line.lineId,
    receiptText: line.receiptText,
    confidence: line.confidence,
    status: signalFn(line.status),
    isNewProduct: line.isNewProduct,
    newProductName: line.newProductName,
    price: signalFn(typeof effectivePrice === "number" ? effectivePrice : null),
    suggestedPrice: signalFn(typeof line.suggestedPrice === "number" ? line.suggestedPrice : null),
    saving: signalFn(false),
    error: signalFn(/** @type {string|null} */ (null)),
    // Matched (Pending+High) rows start closed; user clicks toggle to expand.
    // Unmatched (Pending+Low/None/productId=null) rows start open for attention.
    drawerOpen: signalFn(
      line.status === "Pending" &&
      line.confidence !== "High" &&
      line.productId === null &&
      !line.isNewProduct,
    ),
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
  };
}

// ── lineSection ───────────────────────────────────────────────────────────────

/**
 * Derive which display section a line belongs to.
 *
 * @param {LineState} ls
 * @returns {"needs" | "ready" | "skipped"}
 */
export function lineSection(ls) {
  const s = ls.status.value;
  if (s === "Dismissed") return "skipped";
  if (s === "Confirmed" || s === "Committed") return "ready";
  return "needs";
}

// ── isUnmatched ───────────────────────────────────────────────────────────────

/**
 * Returns true when the line is in a state that needs user attention:
 * Pending + not High confidence. (High-confidence lines are auto-matched
 * and show a quick-confirm button; Low/None lines need manual review.)
 *
 * @param {LineState} ls
 * @returns {boolean}
 */
export function isUnmatched(ls) {
  return ls.status.value === "Pending" && ls.confidence !== "High";
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
