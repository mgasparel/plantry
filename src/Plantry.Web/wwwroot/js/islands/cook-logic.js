// @ts-check
//
// cook-logic.js — pure skip/count predicates for the Cook confirmation island (plantry-xvdo).
//
// The Cook page (Pages/Recipes/Cook.cshtml) lets the user resolve a cook before confirming: skip an
// individual ingredient line, whole-skip an inclusion group, modify a quantity, or add a product. The
// rail's "ingredients" stat and the per-line / per-group greying all derive from ONE authoritative
// notion of "is this line still active?". This module owns that notion as pure functions of their
// arguments so it can be unit-tested (node --test) independently of Alpine/DOM.
//
// WHY THIS FILE IS A CLASSIC GLOBAL, NOT AN ESM `export` MODULE (unlike its siblings):
//   The other island logic modules (intake-review-logic.js, deal-deck-logic.js, …) are imported by
//   ESM island scripts that mount manually. The Cook page is different: its Alpine `x-data="cookConfirm(…)"`
//   factory is a CLASSIC inline <script> that must define the global BEFORE the deferred alpine.min.js
//   runs and evaluates x-data (see _Layout.cshtml: Alpine inits synchronously the moment its deferred
//   script executes, after parse). A `type="module"` in the page's Scripts section would run AFTER
//   alpine.min.js and break x-data. So the Cook page loads this file as a classic (non-deferred) script
//   ahead of the inline factory — both run during parse, before the deferred Alpine.
//
//   To stay the SINGLE SOURCE OF TRUTH (no drift-prone test twin), this file is authored to work in
//   BOTH environments with zero build step: it assigns `globalThis.CookLogic` via an IIFE and uses no
//   import/export syntax. The browser loads it as a classic script (sets window.CookLogic); the node
//   test imports it for its side effect and reads globalThis.CookLogic. The running file is the file
//   you read.
//
// PATH MODEL (recipe-composition.md §6, D6/D7):
//   • A line's identity ("lineKey") is the bare IngredientId for a direct line, or "{PathKey}|{IngredientId}"
//     for an expanded inclusion line, where PathKey is the '/'-joined InclusionId chain (e.g. "A", "A/B").
//   • A whole-inclusion skip is recorded by PathKey. The server (Cook.cshtml.cs WholeInclusionSkip)
//     drops EVERY expanded line beneath that path PREFIX, so the display MUST match by the same prefix
//     predicate — a line/group drops when its PathKey === p OR PathKey.startsWith(p + '/') for any
//     skipped path p. NB the separator is '/', not the '|' that joins path→ingredient in a lineKey:
//     a nested line "A/B|x" drops under skipped "A" via the "A/B".startsWith("A/") chain test.

(function (root) {
  "use strict";

  /**
   * The '/'-joined inclusion PathKey of a line, derived from its lineKey.
   * Direct lines (bare IngredientId, no '|') have the empty path.
   * @param {string} lineKey
   * @returns {string}
   */
  function pathKeyOf(lineKey) {
    const i = lineKey.lastIndexOf("|");
    return i === -1 ? "" : lineKey.slice(0, i);
  }

  /**
   * True when pathKey is dropped by the current set of whole-inclusion skips: it is exactly a skipped
   * path OR a descendant of one ('/'-joined chain). Mirrors the server's prefix-drop consume. The empty
   * path (a direct line / group-less line) is never inclusion-skipped.
   * @param {string} pathKey
   * @param {string[]} skippedPaths  the whole-inclusion skip set (by PathKey)
   * @returns {boolean}
   */
  function isPathUnderSkip(pathKey, skippedPaths) {
    if (!pathKey) return false;
    return skippedPaths.some((p) => p !== "" && (pathKey === p || pathKey.startsWith(p + "/")));
  }

  /**
   * True when a STRICT ANCESTOR of pathKey is whole-skipped (an exact match does NOT count). Used to
   * disable a descendant group's OWN "Skip all" control under a skipped ancestor while leaving that
   * group's own Undo affordance enabled when only it (exactly) is skipped.
   * @param {string} pathKey
   * @param {string[]} skippedPaths
   * @returns {boolean}
   */
  function isAncestorSkipped(pathKey, skippedPaths) {
    if (!pathKey) return false;
    return skippedPaths.some((p) => p !== "" && p !== pathKey && pathKey.startsWith(p + "/"));
  }

  /**
   * AUTHORITATIVE active-line count: count each tracked line exactly ONCE, excluding it when it is
   * individually skipped OR falls under any whole-inclusion skip path. Set-based, so it is correct
   * regardless of skip ordering (skip-line-then-skip-group and the reverse) and for arbitrarily nested
   * inclusions — no double subtraction (a line both individually skipped and under a skipped group is
   * dropped once), and no nested under-count (a descendant line drops under a skipped ancestor).
   * @param {string[]} lineKeys      all tracked line keys (one per tracked line, direct + inclusion)
   * @param {string[]} skippedIds    individually-skipped line keys
   * @param {string[]} skippedPaths  whole-inclusion skip paths (by PathKey)
   * @returns {number}
   */
  function activeLineCount(lineKeys, skippedIds, skippedPaths) {
    const skipped = new Set(skippedIds);
    let n = 0;
    for (const k of lineKeys) {
      if (skipped.has(k)) continue;
      if (isPathUnderSkip(pathKeyOf(k), skippedPaths)) continue;
      n++;
    }
    return n;
  }

  root.CookLogic = { pathKeyOf, isPathUnderSkip, isAncestorSkipped, activeLineCount };
})(typeof globalThis !== "undefined" ? globalThis : this);
