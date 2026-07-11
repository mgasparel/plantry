// @ts-check
//
// Unit tests for cook-logic.js (plantry-xvdo) — the Cook page's authoritative skip/count predicates.
//
// Run with: node --test  (from repo root)  or  npm test
//
// No npm dependencies — Node's built-in test runner + assert. cook-logic.js is a classic global module
// (see its header for why): it assigns globalThis.CookLogic as a side effect of loading, so we import
// it for effect and read the functions off globalThis — the SAME object the browser's inline
// cookConfirm factory consumes.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import "../cook-logic.js";

const { pathKeyOf, isPathUnderSkip, isAncestorSkipped, activeLineCount } =
  /** @type {any} */ (globalThis).CookLogic;

// ── path identity ────────────────────────────────────────────────────────────

describe("pathKeyOf", () => {
  it("returns empty for a direct line (bare ingredient id, no '|')", () => {
    assert.equal(pathKeyOf("11111111-1111-1111-1111-111111111111"), "");
  });

  it("returns the path for a top-level inclusion line", () => {
    assert.equal(pathKeyOf("A|ing1"), "A");
  });

  it("returns the '/'-joined chain for a nested inclusion line", () => {
    assert.equal(pathKeyOf("A/B|ing2"), "A/B");
  });
});

// ── prefix-drop predicate (mirrors the server WholeInclusionSkip consume) ──────

describe("isPathUnderSkip", () => {
  it("is false when nothing is skipped", () => {
    assert.equal(isPathUnderSkip("A", []), false);
  });

  it("drops the exact skipped path", () => {
    assert.equal(isPathUnderSkip("A", ["A"]), true);
  });

  it("drops a descendant of a skipped ancestor ('/'-joined chain)", () => {
    assert.equal(isPathUnderSkip("A/B", ["A"]), true);
    assert.equal(isPathUnderSkip("A/B/C", ["A"]), true);
  });

  it("does NOT drop a sibling whose name merely shares a prefix without the '/' boundary", () => {
    // "AB" must not be treated as under "A" — the boundary is a '/' segment, not a substring.
    assert.equal(isPathUnderSkip("AB", ["A"]), false);
  });

  it("does NOT drop an ancestor when only a descendant is skipped", () => {
    assert.equal(isPathUnderSkip("A", ["A/B"]), false);
  });

  it("never drops the empty (direct-line) path", () => {
    assert.equal(isPathUnderSkip("", ["A"]), false);
  });
});

// ── strict-ancestor predicate (descendant group's own Skip-all disable) ────────

describe("isAncestorSkipped", () => {
  it("is true when a strict ancestor is skipped", () => {
    assert.equal(isAncestorSkipped("A/B", ["A"]), true);
  });

  it("is FALSE for an exact self-skip (so the group's own Undo stays enabled)", () => {
    assert.equal(isAncestorSkipped("A/B", ["A/B"]), false);
  });

  it("is false when only a descendant is skipped", () => {
    assert.equal(isAncestorSkipped("A", ["A/B"]), false);
  });
});

// ── authoritative active count ─────────────────────────────────────────────────

describe("activeLineCount", () => {
  // A flat set of three direct lines + one top-level inclusion "A" with two lines.
  const directLines = ["d1", "d2", "d3"];
  const inclusionA = ["A|a1", "A|a2"];
  const flat = [...directLines, ...inclusionA]; // 5 tracked lines

  it("counts every tracked line once when nothing is skipped", () => {
    assert.equal(activeLineCount(flat, [], []), 5);
  });

  it("subtracts an individually skipped direct line", () => {
    assert.equal(activeLineCount(flat, ["d1"], []), 4);
  });

  it("subtracts a whole-inclusion skip (both its lines drop, once)", () => {
    assert.equal(activeLineCount(flat, [], ["A"]), 3); // only the 3 direct lines remain
  });

  // ── ACCEPTANCE: no double-count from EITHER skip ordering ────────────────────
  // A line under a skipped inclusion that is ALSO individually skipped must be dropped exactly once.

  it("skip-line-then-skip-group: the doubly-skipped line drops once, not twice", () => {
    // Skip inclusion line A|a1 individually, THEN whole-skip inclusion A.
    const skippedIds = ["A|a1"];
    const skippedPaths = ["A"];
    // Active = the 3 direct lines. A|a1 and A|a2 both drop (under A); A|a1 is not subtracted twice.
    assert.equal(activeLineCount(flat, skippedIds, skippedPaths), 3);
  });

  it("skip-group-then-skip-line: same result — order-independent (set-based)", () => {
    // Same end state reached the other way round.
    const skippedIds = ["A|a2"];
    const skippedPaths = ["A"];
    assert.equal(activeLineCount(flat, skippedIds, skippedPaths), 3);
  });

  it("never goes negative when a skipped line is also under a skipped group", () => {
    // Every line skipped one way or another → 0, not a negative from double subtraction.
    const all = [...directLines, ...inclusionA];
    const skippedIds = [...directLines, "A|a1", "A|a2"];
    const skippedPaths = ["A"];
    assert.equal(activeLineCount(all, skippedIds, skippedPaths), 0);
  });

  // ── ACCEPTANCE: 2-level nested inclusion ─────────────────────────────────────
  // Recipe with a top inclusion "A" (one own line) containing a nested inclusion "A/B" (two lines).

  const nested = ["d1", "A|a1", "A/B|b1", "A/B|b2"]; // 1 direct + 1 in A + 2 in A/B

  it("nested: whole-skip of the ANCESTOR drops the ancestor's line AND the nested descendants", () => {
    // Skip "A" → A|a1, A/B|b1, A/B|b2 all drop; only d1 remains.
    assert.equal(activeLineCount(nested, [], ["A"]), 1);
  });

  it("nested: whole-skip of only the NESTED inclusion drops just its two lines", () => {
    // Skip "A/B" → b1, b2 drop; d1 and A|a1 remain.
    assert.equal(activeLineCount(nested, [], ["A/B"]), 2);
  });

  it("nested: nested-line-skip + ancestor whole-skip still drops each line once", () => {
    // Individually skip A/B|b1, then whole-skip ancestor A. Result: only d1 active.
    assert.equal(activeLineCount(nested, ["A/B|b1"], ["A"]), 1);
  });
});
