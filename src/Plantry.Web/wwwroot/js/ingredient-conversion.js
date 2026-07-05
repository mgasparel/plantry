// @ts-check
//
// ingredient-conversion.js — pure math for the recipe-editor "define a conversion against any unit"
// four-field equation (bead plantry-qno9).
//
// The in-sheet prompt lets the author state a cross-measure fact against ANY unit pair ("1 kg = 8 cups"):
// LEFT amount+unit (on the product stock dimension) = RIGHT amount+unit (on the recipe-line dimension).
// This module derives the client-side ECHO — "so 1 cup ≈ 125 g" — that translates the entered equation
// into stock terms so the author can sanity-check the direction. The server stays authoritative for the
// stored factor (factor = rightAmount / leftAmount); this is a courtesy that mirrors resolution live.
//
// WHY A MODULE (not inline x-data): matches ingredient-amount.js / recipe-sections.js — pure logic
// extracted so it runs under the sanctioned zero-dependency `node --test` rig
// (__tests__/ingredient-conversion.test.js), bridged onto window.IngredientConversion by Edit.cshtml and
// called from the editor's Alpine at render time. No build, no transpile — the file that runs is this file.

/**
 * How much of the product's stock/default unit is 1 recipe-line unit, given the author's equation
 * `leftAmount·leftUnit = rightAmount·rightUnit`. Every `*Factor` argument is the unit's factor-to-base
 * within its own dimension — LEFT and the stock default share the stock dimension; RIGHT and the recipe
 * line share the recipe dimension. The two dimensions differ by construction (that is why the prompt
 * appears), so the pair always bridges.
 *
 *   1 recipeLineUnit
 *     = (recipeLineFactor / rightFactor) · (leftAmount / rightAmount) · (leftFactor / stockDefaultFactor) stockDefault
 *
 * e.g. "1 kg = 8 cup", recipe line = cup, stock default = g:
 *   (1/1) · (1/8) · (1000/1) = 125  → 1 cup ≈ 125 g.
 *
 * Returns null when any input is missing, non-finite, or non-positive (so nothing is shown mid-typing).
 *
 * @param {{leftAmount:number|string, rightAmount:number|string, leftFactor:number|string,
 *          rightFactor:number|string, recipeLineFactor:number|string, stockDefaultFactor:number|string}} eq
 * @returns {number|null}
 */
export function stockPerRecipeUnit(eq) {
    const la = Number(eq.leftAmount), ra = Number(eq.rightAmount);
    const lf = Number(eq.leftFactor), rf = Number(eq.rightFactor);
    const rlf = Number(eq.recipeLineFactor), sdf = Number(eq.stockDefaultFactor);
    if (![la, ra, lf, rf, rlf, sdf].every(n => Number.isFinite(n) && n > 0)) return null;
    const result = (rlf / rf) * (la / ra) * (lf / sdf);
    return Number.isFinite(result) && result > 0 ? result : null;
}

/**
 * Render a derived stock amount for the echo line: round to at most 3 fractional digits and strip any
 * trailing zeros. 125 → "125", 124.5 → "124.5". Null / non-positive → "" (nothing to show).
 *
 * @param {number|null} value
 * @returns {string}
 */
export function formatEchoAmount(value) {
    if (value == null || !Number.isFinite(value) || value <= 0) return '';
    const rounded = Math.round(value * 1000) / 1000;
    return String(rounded);
}
