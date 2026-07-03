// @ts-check
//
// Unit tests for recipe-sections.js (bead plantry-vff8, Direction A).
//
// Run with: node --test  (from repo root)  or  npm test
// No npm dependencies — Node's built-in runner + assert, importing the ESM module directly.
//
// Focus (per the ticket's acceptance): cross-section reassignment (placeRow) and the rename
// cascade (renameSection), plus the canonical normalize/derive rules they depend on.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import {
  isUngrouped,
  orderedHeadings,
  normalizeRows,
  deriveSections,
  placeRow,
  renameSection,
  deleteSection,
} from "../recipe-sections.js";

// ── helpers ──────────────────────────────────────────────────────────────────

let seq = 0;
/** @param {string|null} groupHeading @param {string} [name] */
function row(groupHeading, name) {
  const id = seq++;
  return { _id: id, ordinal: id, groupHeading, name: name ?? `r${id}` };
}

/** @param {{_id:number}[]} rows */
const ids = (rows) => rows.map((r) => r._id);
/** @param {{groupHeading:string|null|undefined}[]} rows */
const heads = (rows) => rows.map((r) => r.groupHeading);

// ── isUngrouped ───────────────────────────────────────────────────────────────

describe("isUngrouped", () => {
  it("treats null, undefined, empty and whitespace as ungrouped", () => {
    for (const h of [null, undefined, "", "   ", "\t"]) assert.equal(isUngrouped(h), true);
  });
  it("treats any non-blank string as grouped", () => {
    for (const h of ["Sauce", " x ", "0"]) assert.equal(isUngrouped(h), false);
  });
});

// ── orderedHeadings ────────────────────────────────────────────────────────────

describe("orderedHeadings", () => {
  it("returns distinct non-empty headings in first-appearance order", () => {
    const rows = [row("Sauce"), row(null), row("Topping"), row("Sauce"), row("Topping")];
    assert.deepEqual(orderedHeadings(rows), ["Sauce", "Topping"]);
  });
  it("is case-sensitive (Sauce and sauce are different sections)", () => {
    const rows = [row("Sauce"), row("sauce")];
    assert.deepEqual(orderedHeadings(rows), ["Sauce", "sauce"]);
  });
});

// ── normalizeRows ──────────────────────────────────────────────────────────────

describe("normalizeRows", () => {
  it("puts ungrouped first, then sections first-seen, and reassigns contiguous ordinals", () => {
    // Deliberately interleaved / grouped-first input.
    const a = row("Sauce"), b = row(null), c = row("Topping"), d = row("Sauce"), e = row(null);
    const out = normalizeRows([a, b, c, d, e]);
    // ungrouped (b, e) first, then Sauce (a, d), then Topping (c)
    assert.deepEqual(ids(out), [b._id, e._id, a._id, d._id, c._id]);
    assert.deepEqual(out.map((r) => r.ordinal), [0, 1, 2, 3, 4]);
    // same-group members keep their relative order
    assert.deepEqual(ids(out.filter((r) => r.groupHeading === "Sauce")), [a._id, d._id]);
  });

  it("canonicalizes blank/null ungrouped headings to '' ", () => {
    const out = normalizeRows([row(null), row("   ")]);
    assert.deepEqual(heads(out), ["", ""]);
  });

  it("a recipe with no headings stays a single ungrouped run", () => {
    const rows = [row(null), row(null), row(null)];
    const out = normalizeRows(rows);
    assert.deepEqual(out.map((r) => r.ordinal), [0, 1, 2]);
    assert.deepEqual(heads(out), ["", "", ""]);
  });
});

// ── deriveSections ─────────────────────────────────────────────────────────────

describe("deriveSections", () => {
  it("always yields the ungrouped bucket first, then named sections first-seen", () => {
    const rows = normalizeRows([row("Sauce"), row(null), row("Topping")]);
    const secs = deriveSections(rows);
    assert.deepEqual(secs.map((s) => s.heading), ["", "Sauce", "Topping"]);
    assert.equal(secs[0].isUngrouped, true);
    assert.equal(secs[1].isUngrouped, false);
  });

  it("groups members under the right section", () => {
    const rows = normalizeRows([row("Sauce", "garlic"), row("Sauce", "oil"), row(null, "salt")]);
    const secs = deriveSections(rows);
    assert.deepEqual(secs[0].items.map((r) => r.name), ["salt"]);
    assert.deepEqual(secs[1].items.map((r) => r.name), ["garlic", "oil"]);
  });
});

// ── placeRow — cross-section reassignment (core acceptance) ─────────────────────

describe("placeRow", () => {
  it("moving a row across a boundary reassigns its GroupHeading", () => {
    const salt = row(null, "salt"), garlic = row("Sauce", "garlic"), oil = row("Sauce", "oil");
    let rows = normalizeRows([salt, garlic, oil]);
    // drag `salt` into the Sauce section, before `oil`
    rows = placeRow(rows, salt._id, "Sauce", oil._id);
    const moved = rows.find((r) => r._id === salt._id);
    assert.equal(moved.groupHeading, "Sauce");
    // ungrouped is now empty; Sauce order is garlic, salt, oil
    assert.deepEqual(ids(rows), [garlic._id, salt._id, oil._id]);
    assert.deepEqual(rows.map((r) => r.ordinal), [0, 1, 2]);
  });

  it("dropping with beforeId=null appends to the end of the target section", () => {
    const salt = row(null, "salt"), garlic = row("Sauce", "garlic"), oil = row("Sauce", "oil");
    let rows = normalizeRows([salt, garlic, oil]);
    rows = placeRow(rows, salt._id, "Sauce", null);
    assert.equal(rows.find((r) => r._id === salt._id).groupHeading, "Sauce");
    assert.deepEqual(ids(rows), [garlic._id, oil._id, salt._id]);
  });

  it("moving a row back to Ungrouped clears its heading and floats it to the front", () => {
    const salt = row(null, "salt"), garlic = row("Sauce", "garlic");
    let rows = normalizeRows([salt, garlic]);
    rows = placeRow(rows, garlic._id, "", null);
    assert.equal(rows.find((r) => r._id === garlic._id).groupHeading, "");
    // both ungrouped now, ordinals contiguous
    assert.deepEqual(heads(rows), ["", ""]);
    assert.deepEqual(rows.map((r) => r.ordinal), [0, 1]);
  });

  it("emptying the last member of a section drops the section (no empty sections persisted)", () => {
    const garlic = row("Sauce", "garlic"), salt = row(null, "salt");
    let rows = normalizeRows([garlic, salt]);
    rows = placeRow(rows, garlic._id, "", null);
    assert.deepEqual(orderedHeadings(rows), []); // Sauce is gone
  });

  it("reordering within a section preserves membership and yields Details-equal order", () => {
    const a = row("Sauce", "a"), b = row("Sauce", "b"), c = row("Sauce", "c");
    let rows = normalizeRows([a, b, c]);
    // drag c before a
    rows = placeRow(rows, c._id, "Sauce", a._id);
    assert.deepEqual(ids(rows), [c._id, a._id, b._id]);
    // editor order == the order Details' OrderBy(Ordinal).GroupBy would render
    assert.deepEqual(rows.map((r) => r.ordinal), [0, 1, 2]);
  });

  it("is a no-op-ish normalize when the drag id is unknown", () => {
    const rows = normalizeRows([row("Sauce"), row(null)]);
    const out = placeRow(rows, 9999, "Sauce", null);
    assert.equal(out.length, rows.length);
  });
});

// ── renameSection — rename cascade (core acceptance) ────────────────────────────

describe("renameSection", () => {
  it("renaming a heading once updates every member of that section", () => {
    const a = row("Sauce", "a"), b = row("Sauce", "b"), c = row(null, "c");
    let rows = normalizeRows([a, b, c]);
    rows = renameSection(rows, "Sauce", "Gravy");
    assert.deepEqual(
      rows.filter((r) => r.name === "a" || r.name === "b").map((r) => r.groupHeading),
      ["Gravy", "Gravy"],
    );
    assert.deepEqual(orderedHeadings(rows), ["Gravy"]);
  });

  it("a blank new name reverts (no-op)", () => {
    const a = row("Sauce", "a");
    let rows = normalizeRows([a]);
    rows = renameSection(rows, "Sauce", "   ");
    assert.equal(rows[0].groupHeading, "Sauce");
  });

  it("renaming onto an existing heading merges the two sections", () => {
    const a = row("Sauce", "a"), b = row("Topping", "b");
    let rows = normalizeRows([a, b]);
    rows = renameSection(rows, "Topping", "Sauce");
    assert.deepEqual(orderedHeadings(rows), ["Sauce"]);
    assert.equal(rows.every((r) => r.groupHeading === "Sauce"), true);
  });

  it("renaming the ungrouped bucket is a no-op", () => {
    const a = row(null, "a");
    let rows = normalizeRows([a]);
    rows = renameSection(rows, "", "Whatever");
    assert.equal(rows[0].groupHeading, "");
  });
});

// ── deleteSection ───────────────────────────────────────────────────────────────

describe("deleteSection", () => {
  it("moves the section's rows to Ungrouped, preserving the ingredients", () => {
    const a = row("Sauce", "a"), b = row("Sauce", "b"), c = row(null, "c");
    let rows = normalizeRows([a, b, c]);
    rows = deleteSection(rows, "Sauce");
    assert.equal(rows.length, 3); // nothing deleted
    assert.deepEqual(orderedHeadings(rows), []);
    assert.equal(rows.every((r) => r.groupHeading === ""), true);
  });
});
