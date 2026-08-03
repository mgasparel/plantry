// @ts-check
//
// Unit tests for star-rating.js (bead plantry-zlwp.2).
//
// Run with: node --test  (from repo root)  or  npm test
// No npm dependencies — Node's built-in runner + assert, importing the ESM module directly.
//
// Covers the interaction contract that has no other test coverage: rate()'s tap-current-again-
// to-clear toggle (the ticket's own acceptance clause, and the epic's "no opinion = absence of a
// row" storage decision), isFilled()'s hover-wins preview rule, and hint()'s pluralisation /
// cleared-state text. persist() is deliberately NOT exercised here — it is the single
// htmx/DOM-touching seam (mirrors the ingredient-amount.js / recipe-sections.js precedent of
// leaving the DOM-facing bridge untested and unit-testing only the pure state); these tests never
// touch `document` or `htmx`, and cases here supply no postUrl so persist() is a no-op.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import { createStarRating } from "../star-rating.js";

// ── initial state ────────────────────────────────────────────────────────────────────────────

describe("createStarRating — initial state", () => {
  it("defaults value to 0 (unrated) when cfg.value is omitted", () => {
    const s = createStarRating({});
    assert.equal(s.value, 0);
  });

  it("seeds value from cfg.value", () => {
    const s = createStarRating({ value: 4 });
    assert.equal(s.value, 4);
  });

  it("defaults postUrl to null and fieldName to 'stars'", () => {
    const s = createStarRating({});
    assert.equal(s.postUrl, null);
    assert.equal(s.fieldName, "stars");
  });

  it("carries a custom postUrl and name through", () => {
    const s = createStarRating({ postUrl: "/Recipes/Details?handler=Rate", name: "rating" });
    assert.equal(s.postUrl, "/Recipes/Details?handler=Rate");
    assert.equal(s.fieldName, "rating");
  });
});

// ── rate() — tap-current-again-to-clear toggle ──────────────────────────────────────────────────
// The ticket's own acceptance clause ("tap current again to clear") and the epic's storage
// decision ("no opinion = absence of a row", i.e. clearing sets 0).

describe("rate() — tap to set, tap current again to clear", () => {
  it("tapping an unrated widget sets the tapped star", () => {
    const s = createStarRating({ value: 0 });
    s.rate(3);
    assert.equal(s.value, 3);
  });

  it("tapping a different star than the current rating replaces it", () => {
    const s = createStarRating({ value: 4 });
    s.rate(2);
    assert.equal(s.value, 2);
  });

  it("tapping the CURRENT rating again clears it to 0", () => {
    const s = createStarRating({ value: 4 });
    s.rate(4);
    assert.equal(s.value, 0);
  });

  it("clearing then re-tapping the same star sets it again (toggle, not sticky-clear)", () => {
    const s = createStarRating({ value: 4 });
    s.rate(4); // clear
    assert.equal(s.value, 0);
    s.rate(4); // set again
    assert.equal(s.value, 4);
  });

  it("is a no-op network-wise when postUrl is omitted (persist short-circuits)", () => {
    const s = createStarRating({ value: 0 });
    // No postUrl supplied: persist() must return before touching htmx/document. Rating still
    // applies locally either way — this only pins that rate() does not throw with no postUrl.
    assert.doesNotThrow(() => s.rate(5));
    assert.equal(s.value, 5);
  });
});

// ── isFilled() — hover-wins preview rule ────────────────────────────────────────────────────────

describe("isFilled() — hover preview takes priority over the committed value", () => {
  it("with no hover, reflects the committed value", () => {
    const s = createStarRating({ value: 3 });
    assert.equal(s.isFilled(1), true);
    assert.equal(s.isFilled(3), true);
    assert.equal(s.isFilled(4), false);
  });

  it("while hovering, the hover value wins even if lower than the committed value", () => {
    const s = createStarRating({ value: 4 });
    s.hoverValue = 2;
    assert.equal(s.isFilled(2), true);
    assert.equal(s.isFilled(3), false); // committed value (4) is ignored while hovering
  });

  it("while hovering, the hover value wins even if higher than the committed value", () => {
    const s = createStarRating({ value: 1 });
    s.hoverValue = 5;
    assert.equal(s.isFilled(5), true);
  });

  it("clearing hover (hoverValue = 0) falls back to the committed value", () => {
    const s = createStarRating({ value: 3 });
    s.hoverValue = 5;
    s.hoverValue = 0;
    assert.equal(s.isFilled(4), false);
    assert.equal(s.isFilled(3), true);
  });
});

// ── hint() — pluralisation and cleared-state text ───────────────────────────────────────────────

describe("hint()", () => {
  it("returns 'Tap to rate' when unrated", () => {
    const s = createStarRating({ value: 0 });
    assert.equal(s.hint(), "Tap to rate");
  });

  it("uses singular 'star' at 1", () => {
    const s = createStarRating({ value: 1 });
    assert.equal(s.hint(), "You rated this 1 star — tap again to clear");
  });

  it("uses plural 'stars' at 2 and up", () => {
    for (const value of [2, 3, 4, 5]) {
      const s = createStarRating({ value });
      assert.equal(s.hint(), `You rated this ${value} stars — tap again to clear`);
    }
  });

  it("reverts to 'Tap to rate' after rate() clears the rating", () => {
    const s = createStarRating({ value: 4 });
    s.rate(4); // clear
    assert.equal(s.hint(), "Tap to rate");
  });
});
