// @ts-check
//
// ingredient-anchor.js — pure hash-fragment parsing for the recipe editor's per-line fix link
// (bead plantry-c7mg, D2 tidy-up.md §3 / T3).
//
// Tidy Up's D2 detector links to `/Recipes/{id}/Edit#ingredient-{ordinal}` so a flagged conversion
// gap opens the editor scrolled and highlighted on the exact offending line. Ingredient rows are
// client-rendered by Alpine (recipe-composition.md), so the element does not exist at document
// load — a native URL fragment cannot scroll to it. The editor's own init() reads location.hash
// AFTER rows first render and drives the scroll/flash itself; this module holds only the pure
// hash → ordinal parse so it can be unit-tested with the sanctioned zero-dependency `node --test`
// rig (see __tests__/ingredient-anchor.test.js). The scroll/flash DOM side effect stays inline in
// Edit.cshtml (mirrors islands/intake-review.js's railJump "sure" branch) — it needs a real
// document and is covered by the existing Playwright E2E suite instead.
//
// WHY A MODULE (not an island): the recipe editor is a hypermedia + Alpine page, NOT an ADR-020
// reactive island. This file carries no reactive runtime; it is bridged onto
// window.IngredientAnchor by Edit.cshtml and called from the page's Alpine init() at load time.

/**
 * Parse a URL hash fragment for an ingredient-row anchor. Returns the 0-based ordinal encoded in
 * `#ingredient-{n}`, or null if the hash does not match (missing, malformed, non-integer, or
 * negative — ordinals are never negative).
 *
 * @param {string|null|undefined} hash - e.g. location.hash, with or without the leading "#".
 * @returns {number|null}
 */
export function parseIngredientAnchor(hash) {
  if (!hash) return null;
  const match = /^#?ingredient-(\d+)$/.exec(hash);
  if (!match) return null;
  const ordinal = Number(match[1]);
  return Number.isInteger(ordinal) && ordinal >= 0 ? ordinal : null;
}
