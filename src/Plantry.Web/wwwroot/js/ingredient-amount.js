// @ts-check
//
// ingredient-amount.js — canonical client-side rendering of ingredient amounts (bead plantry-jun6,
// plantry-95w5).
//
// The JS twin of C# QuantityDisplay.FormatAmount (src/Plantry.Catalog/Domain/QuantityDisplay.cs) — the
// Q1 vulgar-fraction snap only (Details never does Q2 cross-unit re-expression, even server-side, so the
// JS twin mirrors just the snap rule, not full Simplify). The Details page scales ingredient quantities
// by servings on the client, so the amount is rendered in the browser; this module keeps that client
// render in lock-step with the server rule so a server-rendered amount and a scaled client-rendered
// amount never disagree.
//
// WHY A MODULE (not an island): the recipe detail page is a hypermedia + Alpine page, NOT one of the
// ADR-020 reactive island surfaces. This file carries no reactive runtime; it is bridged onto
// window.IngredientAmount by Details.cshtml and called from the page's Alpine fmt() at render time.
// Extracting the rule here (rather than inlining it in x-data) is purely so it can be unit-tested with
// the sanctioned zero-dependency `node --test` rig (see __tests__/ingredient-amount.test.js). No build,
// no transpile — the file that runs is this file.
//
// Rule: for style "fraction" and a positive amount, attempt the same vulgar-fraction snap
// QuantityDisplay.FormatAmount does (vocabulary ½ ¼ ¾ ⅓ ⅛ ⅔ ⅜ ⅝ ⅞, SNAP_TOLERANCE); when the remainder
// snaps to no vocabulary fraction (or style is "decimal"/omitted, or the amount is non-positive), fall
// back to the historical rule: round to at most MAX_DECIMALS fractional digits, then strip trailing
// zeros. 500 → "500", 1.5 → "1.5", 0.125 → "0.125" (decimal style) / "⅛" (fraction style).

/** Maximum fractional digits retained before trailing zeros are stripped. Matches C# IngredientAmount.MaxDecimals. */
export const MAX_DECIMALS = 4;

/**
 * A fractional remainder must be within this of a vocabulary fraction to snap. Matches C#
 * QuantityDisplay.SnapTolerance.
 */
export const SNAP_TOLERANCE = 0.01;

/**
 * The vulgar-fraction vocabulary (quantity-display.md Q3), mirroring C# QuantityDisplay.Vocabulary.
 * Order is not significant — nearest-by-value wins the snap.
 * @type {{value: number, glyph: string}[]}
 */
const VULGAR_FRACTIONS = [
  { value: 1 / 2, glyph: "½" },
  { value: 1 / 4, glyph: "¼" },
  { value: 3 / 4, glyph: "¾" },
  { value: 1 / 3, glyph: "⅓" },
  { value: 1 / 8, glyph: "⅛" },
  { value: 2 / 3, glyph: "⅔" },
  { value: 3 / 8, glyph: "⅜" },
  { value: 5 / 8, glyph: "⅝" },
  { value: 7 / 8, glyph: "⅞" },
];

/**
 * Splits amount into a whole part and a vocabulary fraction glyph if the remainder snaps within
 * SNAP_TOLERANCE, mirroring C# QuantityDisplay.TrySnap. Returns the rendered string ("½", "1¾", "2",
 * "4") or null when the remainder matches no vocabulary fraction (the decimal fallback then applies).
 * @param {number} amount
 * @returns {string|null}
 */
function trySnap(amount) {
  const whole = Math.floor(amount);
  const remainder = amount - whole;

  if (remainder <= SNAP_TOLERANCE) return String(whole);
  if (remainder >= 1 - SNAP_TOLERANCE) return String(whole + 1);

  let nearest = null;
  let nearestDiff = Infinity;
  for (const vf of VULGAR_FRACTIONS) {
    const diff = Math.abs(remainder - vf.value);
    if (diff < nearestDiff) {
      nearestDiff = diff;
      nearest = vf;
    }
  }

  if (nearest !== null && nearestDiff <= SNAP_TOLERANCE) {
    return whole === 0 ? nearest.glyph : String(whole) + nearest.glyph;
  }

  return null;
}

/**
 * Format an ingredient amount for display. When `style` is `"fraction"` and the amount is positive,
 * attempts the Q1 vulgar-fraction snap (mirroring C# QuantityDisplay.FormatAmount); otherwise — and
 * whenever the snap does not apply — rounds to at most MAX_DECIMALS fractional digits then strips
 * trailing zeros / a bare trailing decimal point. `style` is optional and defaults to the historical
 * decimal-only render, so existing callers that don't pass it are unaffected.
 * Null / empty / non-finite inputs render as an empty string (nothing to show).
 *
 * @param {number|string|null|undefined} value
 * @param {"fraction"|"decimal"|undefined} [style]
 * @returns {string}
 */
export function formatAmount(value, style) {
  if (value === null || value === undefined || value === "") return "";
  const n = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(n)) return "";

  if (style === "fraction" && n > 0) {
    const snapped = trySnap(n);
    if (snapped !== null) return snapped;
  }

  // toFixed rounds to MAX_DECIMALS places; Number(...) then drops the trailing zeros toFixed pads on
  // (e.g. "500.0000" → 500 → "500", "1.5000" → 1.5 → "1.5").
  return Number(n.toFixed(MAX_DECIMALS)).toString();
}
