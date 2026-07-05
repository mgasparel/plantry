// @ts-check
//
// ingredient-amount.js — canonical client-side rendering of ingredient amounts (bead plantry-jun6).
//
// The JS twin of C# IngredientAmount.Format (src/Plantry.Web/Pages/Recipes/IngredientAmount.cs).
// The Details page scales ingredient quantities by servings on the client, so the amount is rendered
// in the browser; this module keeps that client render in lock-step with the server rule so a
// server-rendered amount and a scaled client-rendered amount never disagree.
//
// WHY A MODULE (not an island): the recipe detail page is a hypermedia + Alpine page, NOT one of the
// ADR-020 reactive island surfaces. This file carries no reactive runtime; it is bridged onto
// window.IngredientAmount by Details.cshtml and called from the page's Alpine fmt() at render time.
// Extracting the rule here (rather than inlining it in x-data) is purely so it can be unit-tested with
// the sanctioned zero-dependency `node --test` rig (see __tests__/ingredient-amount.test.js). No build,
// no transpile — the file that runs is this file.
//
// v1 rule (mirrors the C# twin): round to at most MAX_DECIMALS fractional digits, then strip trailing
// zeros. Kept isolated so future expansion (fractions, unit-aware rendering) has one home.

/** Maximum fractional digits retained before trailing zeros are stripped. Matches C# IngredientAmount.MaxDecimals. */
export const MAX_DECIMALS = 4;

/**
 * Format an ingredient amount: round to at most MAX_DECIMALS fractional digits, then strip trailing
 * zeros / a bare trailing decimal point. 500 → "500", 1.5 → "1.5", 0.125 → "0.125", 100/3 → "33.3333".
 * Null / empty / non-finite inputs render as an empty string (nothing to show).
 *
 * @param {number|string|null|undefined} value
 * @returns {string}
 */
export function formatAmount(value) {
  if (value === null || value === undefined || value === "") return "";
  const n = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(n)) return "";
  // toFixed rounds to MAX_DECIMALS places; Number(...) then drops the trailing zeros toFixed pads on
  // (e.g. "500.0000" → 500 → "500", "1.5000" → 1.5 → "1.5").
  return Number(n.toFixed(MAX_DECIMALS)).toString();
}
