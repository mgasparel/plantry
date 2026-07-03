// @ts-check
//
// recipe-sections.js — pure section transforms for the sectioned recipe-ingredient editor
// (bead plantry-vff8, Direction A). These are plain, dependency-free functions that operate on an
// array of ingredient rows and keep it in the ONE canonical order the Details page renders with:
//
//     Ungrouped rows first, then each distinct GroupHeading in first-appearance order, rows within
//     a section preserving their relative order — matching Details' OrderBy(Ordinal).GroupBy(GroupHeading).
//
// Every mutation returns the array re-normalized with `ordinal` reassigned to the 0-based index, so
// the flat order the editor posts (hidden Input.Lines[i].Ordinal) is exactly what Details will
// group and render — the editor-vs-detail snap-back is gone.
//
// WHY A MODULE (not an island): the recipe editor is a hypermedia + Alpine page, NOT one of the
// three ADR-020 island surfaces. This file carries no reactive runtime (no Preact/signals); it is
// bridged to `window.RecipeSections` by Edit.cshtml and called from the page's Alpine handlers at
// interaction time. Extracting the transforms here (rather than inlining them in the x-data) is
// purely so they can be unit-tested with the sanctioned zero-dependency `node --test` rig
// (see __tests__/recipe-sections.test.js). No build, no transpile — the file that runs is this file.
//
// A "row" is any object with a mutable `groupHeading` (string | null | undefined; blank/whitespace =
// ungrouped), a stable `_id`, and a numeric `ordinal`. These functions only touch those three fields.

/**
 * @typedef {Object} Row
 * @property {number} _id            Stable client id (never reused within a session).
 * @property {number} ordinal        0-based position; reassigned by every transform here.
 * @property {string|null|undefined} groupHeading  Section heading; blank/whitespace = ungrouped.
 */

/**
 * A heading is "ungrouped" when it is null/undefined or trims to the empty string. Mirrors the
 * server's `string.IsNullOrWhiteSpace(GroupHeading)` rule (Edit.cshtml.cs) and Details' null key.
 * @param {string|null|undefined} h
 * @returns {boolean}
 */
export function isUngrouped(h) {
  return h == null || String(h).trim() === "";
}

/**
 * Distinct non-empty headings in first-appearance order (the order Details' GroupBy preserves).
 * @param {Row[]} rows
 * @returns {string[]}
 */
export function orderedHeadings(rows) {
  const seen = [];
  for (const r of rows) {
    if (isUngrouped(r.groupHeading)) continue;
    if (!seen.includes(r.groupHeading)) seen.push(r.groupHeading);
  }
  return seen;
}

/**
 * Re-order `rows` into canonical section order (ungrouped first, then headings first-seen, each
 * section preserving its members' relative order) and reassign `ordinal = index`. Ungrouped rows
 * have their heading canonicalized to '' so the posted value is stable. Mutates each row's `ordinal`
 * (and blank headings) in place and returns a NEW array in canonical order.
 * @param {Row[]} rows
 * @returns {Row[]}
 */
export function normalizeRows(rows) {
  const result = [];
  for (const r of rows) {
    if (isUngrouped(r.groupHeading)) {
      r.groupHeading = "";
      result.push(r);
    }
  }
  for (const h of orderedHeadings(rows)) {
    for (const r of rows) {
      if (!isUngrouped(r.groupHeading) && r.groupHeading === h) result.push(r);
    }
  }
  result.forEach((r, i) => { r.ordinal = i; });
  return result;
}

/**
 * Display sections in render order: the ungrouped bucket (heading '') first, then each named
 * heading in first-appearance order. Each section carries its member rows (same object references).
 * The caller decides whether to render the ungrouped header (see Edit.cshtml — hidden when it is the
 * only section). Kept here so the grouping rule has one tested definition; the page mirrors a thin
 * inline version for its first synchronous render (before this module is bridged onto window).
 * @param {Row[]} rows
 * @returns {{ key: string, heading: string, isUngrouped: boolean, items: Row[] }[]}
 */
export function deriveSections(rows) {
  const sections = [{
    key: "__ungrouped__",
    heading: "",
    isUngrouped: true,
    items: rows.filter((r) => isUngrouped(r.groupHeading)),
  }];
  for (const h of orderedHeadings(rows)) {
    sections.push({
      key: "g:" + h,
      heading: h,
      isUngrouped: false,
      items: rows.filter((r) => r.groupHeading === h),
    });
  }
  return sections;
}

/**
 * Move the dragged row (by `_id`) so it sits immediately before the row `beforeId` within the target
 * section, reassigning its `groupHeading` to `targetHeading` (cross-section reassignment). When
 * `beforeId` is null the row is appended to the end of the target section. Returns the re-normalized
 * array. This is the live-shift primitive: crossing into another section reassigns the group on the fly.
 * @param {Row[]} rows
 * @param {number} dragId
 * @param {string|null|undefined} targetHeading
 * @param {number|null} beforeId
 * @returns {Row[]}
 */
export function placeRow(rows, dragId, targetHeading, beforeId) {
  const dragged = rows.find((r) => r._id === dragId);
  if (!dragged) return normalizeRows(rows);

  const heading = isUngrouped(targetHeading) ? "" : targetHeading;
  dragged.groupHeading = heading;

  const rest = rows.filter((r) => r._id !== dragId);
  let insertAt;
  if (beforeId != null) {
    insertAt = rest.findIndex((r) => r._id === beforeId);
    if (insertAt < 0) insertAt = rest.length;
  } else {
    // Append after the last row currently in the target section.
    insertAt = rest.length;
    let lastInSection = -1;
    for (let i = 0; i < rest.length; i++) {
      const h = isUngrouped(rest[i].groupHeading) ? "" : rest[i].groupHeading;
      if (h === heading) lastInSection = i;
    }
    if (lastInSection >= 0) insertAt = lastInSection + 1;
  }
  rest.splice(insertAt, 0, dragged);
  return normalizeRows(rest);
}

/**
 * Rename a section: every member of `oldHeading` gets `newName` (the rename cascade). A blank
 * `newName` reverts (no-op); renaming to an existing heading merges the two sections. Renaming the
 * ungrouped bucket is a no-op. Returns the re-normalized array.
 * @param {Row[]} rows
 * @param {string} oldHeading
 * @param {string|null|undefined} newName
 * @returns {Row[]}
 */
export function renameSection(rows, oldHeading, newName) {
  if (isUngrouped(oldHeading)) return normalizeRows(rows);
  const trimmed = (newName == null ? "" : String(newName)).trim();
  const target = trimmed === "" ? oldHeading : trimmed;
  for (const r of rows) {
    if (r.groupHeading === oldHeading) r.groupHeading = target;
  }
  return normalizeRows(rows);
}

/**
 * Delete a section: its rows fall back to Ungrouped (heading cleared) — the ingredients are never
 * removed. Deleting the ungrouped bucket is a no-op. Returns the re-normalized array.
 * @param {Row[]} rows
 * @param {string} heading
 * @returns {Row[]}
 */
export function deleteSection(rows, heading) {
  if (isUngrouped(heading)) return normalizeRows(rows);
  for (const r of rows) {
    if (r.groupHeading === heading) r.groupHeading = "";
  }
  return normalizeRows(rows);
}
