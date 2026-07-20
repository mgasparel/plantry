// @ts-check
//
// meal-planner-logic.js — pure transforms for the Meal Planner island (ADR-020, bead plantry-2zvm.12).
//
// CONVENTION (island testing):
//   Pure transforms are extracted into a sibling `*-logic.js` module.
//   The island (`meal-planner.js`) imports and calls them.
//   Tests (`__tests__/meal-planner-logic.test.js`) import from here using
//   `node --test` (built-in, zero deps).
//   This keeps the island file focused on wiring/rendering; the running file
//   is still the file you read (no build, no transpile).
//
// What belongs here (ADR-020 §2 / §7 boundary):
//   UI/display helpers — fulfillment level classification, currency formatting,
//   and dish metadata string assembly. These are pure functions of their arguments
//   and hold NO domain logic. They do not compute fulfillment, compute cost from
//   parts, perform validation-as-truth, or implement any catalog/unit-semantics rule.
//
// What does NOT belong here:
//   Anything that crosses the ADR-020 §7 tripwire. If you need domain rules,
//   call a server endpoint instead.
//
// applyMutationResult is intentionally excluded: it manipulates the live DOM
//   (regex parse, document.createElement, replaceWith, htmx.process,
//   Alpine.initTree) and therefore requires a real browser environment. It is not
//   a pure transform and cannot be meaningfully tested with node:test alone.
//   Its coverage is left to the existing Playwright E2E suite.
//
// §7 adjudication — dishMeta cost arithmetic:
//   `dishMeta` multiplies `d.costPerServing * (d.servings || 1)` solely for
//   display formatting. `costPerServing` is a server-provided per-dish display
//   field (DishDraft.costPerServing — "per-dish cost from server (display-only)").
//   The island does NOT compute costPerServing from component parts; the server
//   delivers it already derived. Multiplying by a servings count that the island
//   holds as draft UI state to produce a display total is an allowed display sum —
//   the same pattern as computing a row total from a server-provided unit price and
//   a user-entered quantity. This is NOT a §7 breach.

/**
 * @typedef {Object} DishDraft
 * @property {"recipe"|"product"} kind
 * @property {string} itemId
 * @property {string} name
 * @property {number} servings
 * @property {number|null} fulfillment       per-dish fulfillment % from server (display-only)
 * @property {number|null} costPerServing    per-dish cost from server (display-only)
 * @property {boolean} hasPhoto
 */

// ── lvl ───────────────────────────────────────────────────────────────────────

/**
 * Classify a fulfillment percentage into a display level.
 *
 * Thresholds: >= 80 → "hi", >= 50 → "mid", otherwise (< 50 or null) → "lo".
 * null means no fulfillment data — treated as "lo".
 *
 * @param {number|null} p
 * @returns {"hi"|"mid"|"lo"}
 */
export function lvl(p) {
  if (p === null) return "lo";
  return p >= 80 ? "hi" : p >= 50 ? "mid" : "lo";
}

// ── money ─────────────────────────────────────────────────────────────────────

/**
 * Format a number as a money amount with exactly two decimal places, prefixed by the
 * household display-currency symbol.
 *
 * Uses Number.prototype.toFixed(2) — standard JS rounding applies (rounds to nearest,
 * ties round up). The symbol comes from the server hydration payload (MoneyDisplay.Symbol,
 * plantry-2x6e.3); there is no currency map in JS. Defaults to "$" when absent — a defensive
 * fallback only; the server always sends the symbol.
 *
 * @param {number} n
 * @param {string} [symbol] household currency symbol (defaults to "$")
 * @returns {string}
 */
export function money(n, symbol = "$") {
  return symbol + n.toFixed(2);
}

// ── dishMeta ─────────────────────────────────────────────────────────────────

/**
 * Build the dish metadata display string shown below each dish in the editor.
 *
 * Format:
 *   - fulfillment === null → "pantry item" (product dishes have no fulfillment)
 *   - otherwise → "{fulfillment}% in pantry"
 *     - if costPerServing is non-null, appended with " · {money(costPerServing * servings)}"
 *
 * §7 adjudication: `costPerServing` is a server-provided display field (per
 * DishDraft typedef). The island multiplies it by `servings` (UI draft state) to
 * produce a formatted total for display only. This is an allowed display sum —
 * the server owns the cost domain; the island only formats what the server gave it.
 * See module header for the full adjudication.
 *
 * @param {DishDraft} d
 * @param {string} [symbol] household currency symbol threaded to money() (defaults to "$")
 * @returns {string}
 */
export function dishMeta(d, symbol = "$") {
  if (d.fulfillment === null) return "pantry item";
  let s = d.fulfillment + "% in pantry";
  if (d.costPerServing !== null) s += " · " + money(d.costPerServing * (d.servings || 1), symbol);
  return s;
}
