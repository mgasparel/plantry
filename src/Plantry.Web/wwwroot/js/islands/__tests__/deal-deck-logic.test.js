// @ts-check
//
// Unit tests for deal-deck-logic.js (ADR-020 fourth-island amendment, bead plantry-q9zr.8).
//
// Run with: node --test  (from repo root)  or  npm test
//
// No npm dependencies — Node's built-in test runner and assert module. Imports the deck
// logic module directly as ESM; the transforms are pure (no browser globals, no DOM).
//
// The island's imperative DOM wiring (pointer events, htmx.ajax verb posts, the shared
// Correct-sheet dispatch, sessionStorage persistence) is NOT tested here — those are DOM /
// network side effects. The three behaviours the bead calls out — deck ROTATION, the swipe
// THRESHOLD, and verb-aware BACK scope — plus deck rebuild, progress, and escaping are the
// pure sub-steps and are covered below.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import {
  DECK_SWIPE_THRESHOLD,
  buildDeckOrder,
  rotateToEnd,
  applySkip,
  applyBack,
  reconcileSkipStack,
  swipeVerb,
  stampOpacity,
  cardTransform,
  nextBaseline,
  deckProgress,
  escapeHtml,
} from "../deal-deck-logic.js";

// ── buildDeckOrder — rebuild from tier truth, preserve skip order ───────────────

describe("buildDeckOrder", () => {
  it("returns server order on a first build (no prior order)", () => {
    assert.deepEqual(buildDeckOrder(["a", "b", "c"]), ["a", "b", "c"]);
    assert.deepEqual(buildDeckOrder(["a", "b", "c"], []), ["a", "b", "c"]);
  });

  it("preserves the prior (skip-rotated) order of still-eligible cards", () => {
    // Prior deck was rotated so 'a' went to the end; a re-mount must keep that order.
    assert.deepEqual(buildDeckOrder(["a", "b", "c"], ["b", "c", "a"]), ["b", "c", "a"]);
  });

  it("drops ids no longer eligible and appends newly eligible ones at the end", () => {
    // 'a' resolved (gone from tier truth); 'd' was just demoted in — it lands last.
    assert.deepEqual(buildDeckOrder(["b", "c", "d"], ["b", "a", "c"]), ["b", "c", "d"]);
  });

  it("does not mutate the inputs", () => {
    const eligible = ["a", "b"];
    const prior = ["b", "a"];
    buildDeckOrder(eligible, prior);
    assert.deepEqual(eligible, ["a", "b"]);
    assert.deepEqual(prior, ["b", "a"]);
  });
});

// ── rotateToEnd / applySkip — the skip motion ───────────────────────────────────

describe("rotateToEnd", () => {
  it("moves the top card to the end", () => {
    assert.deepEqual(rotateToEnd(["a", "b", "c"]), ["b", "c", "a"]);
  });

  it("is a no-op for 0 or 1 card", () => {
    assert.deepEqual(rotateToEnd([]), []);
    assert.deepEqual(rotateToEnd(["a"]), ["a"]);
  });

  it("does not mutate the input", () => {
    const order = ["a", "b", "c"];
    rotateToEnd(order);
    assert.deepEqual(order, ["a", "b", "c"]);
  });
});

describe("applySkip", () => {
  it("rotates the top to the end and records it on the skip stack", () => {
    const result = applySkip(["a", "b", "c"], []);
    assert.deepEqual(result.order, ["b", "c", "a"]);
    assert.deepEqual(result.skipStack, ["a"]);
  });

  it("accumulates multiple skips in order", () => {
    let state = { order: ["a", "b", "c"], skipStack: [] };
    state = applySkip(state.order, state.skipStack); // skip a
    state = applySkip(state.order, state.skipStack); // skip b
    assert.deepEqual(state.order, ["c", "a", "b"]);
    assert.deepEqual(state.skipStack, ["a", "b"]);
  });

  it("is a no-op when 1 or 0 cards remain (nothing to rotate, nothing pushed)", () => {
    assert.deepEqual(applySkip(["a"], []), { order: ["a"], skipStack: [] });
    assert.deepEqual(applySkip([], ["x"]), { order: [], skipStack: ["x"] });
  });
});

// ── applyBack — verb-aware BACK is skip-undo ONLY ───────────────────────────────

describe("applyBack (skip-undo scope)", () => {
  it("restores the most-recently-skipped card to the front", () => {
    // After skipping 'a', the deck is ['b','c','a']; Back brings 'a' back to the front.
    const result = applyBack(["b", "c", "a"], ["a"]);
    assert.deepEqual(result.order, ["a", "b", "c"]);
    assert.deepEqual(result.skipStack, []);
  });

  it("unwinds skips one at a time, most-recent first", () => {
    // Skipped a then b: deck ['c','a','b'], stack ['a','b'].
    let state = applyBack(["c", "a", "b"], ["a", "b"]); // undo skip of b
    assert.deepEqual(state.order, ["b", "c", "a"]);
    assert.deepEqual(state.skipStack, ["a"]);
    state = applyBack(state.order, state.skipStack); // undo skip of a
    assert.deepEqual(state.order, ["a", "b", "c"]);
    assert.deepEqual(state.skipStack, []);
  });

  it("is a no-op with an empty skip stack (Confirm/Reject are final — no inverse)", () => {
    const order = ["a", "b", "c"];
    const result = applyBack(order, []);
    assert.deepEqual(result.order, ["a", "b", "c"]);
    assert.deepEqual(result.skipStack, []);
  });

  it("never removes a card and never changes deck membership", () => {
    const before = ["b", "c", "a"];
    const result = applyBack(before, ["a"]);
    assert.deepEqual([...result.order].sort(), [...before].sort());
    assert.equal(result.order.length, before.length);
  });

  it("discards a skipped id that already left the deck, leaving order untouched", () => {
    // 'a' was skipped then later confirmed on another card's re-render — it is gone from truth.
    const result = applyBack(["b", "c"], ["a"]);
    assert.deepEqual(result.order, ["b", "c"]);
    assert.deepEqual(result.skipStack, []);
  });
});

describe("reconcileSkipStack", () => {
  it("drops skip ids no longer present in the deck", () => {
    assert.deepEqual(reconcileSkipStack(["a", "b", "c"], ["b", "c"]), ["b", "c"]);
  });

  it("keeps the stack intact when every id is still present", () => {
    assert.deepEqual(reconcileSkipStack(["a", "b"], ["b", "a", "c"]), ["a", "b"]);
  });
});

// ── swipeVerb — the 120px threshold ─────────────────────────────────────────────

describe("swipeVerb", () => {
  it("defaults the threshold to 120px", () => {
    assert.equal(DECK_SWIPE_THRESHOLD, 120);
  });

  it("commits confirm only PAST +threshold", () => {
    assert.equal(swipeVerb(121), "confirm");
    assert.equal(swipeVerb(200), "confirm");
  });

  it("commits reject only PAST −threshold", () => {
    assert.equal(swipeVerb(-121), "reject");
    assert.equal(swipeVerb(-200), "reject");
  });

  it("springs back (null) at or under the threshold in either direction", () => {
    assert.equal(swipeVerb(120), null); // exactly at the bar → spring-back
    assert.equal(swipeVerb(60), null); // the AC's under-threshold drag
    assert.equal(swipeVerb(0), null);
    assert.equal(swipeVerb(-60), null);
    assert.equal(swipeVerb(-120), null);
  });

  it("honours a custom threshold", () => {
    assert.equal(swipeVerb(60, 50), "confirm");
    assert.equal(swipeVerb(60, 80), null);
  });
});

// ── stampOpacity / cardTransform — swipe visuals ────────────────────────────────

describe("stampOpacity", () => {
  it("ramps the confirm stamp in with rightward drag, clamped to 1 at threshold", () => {
    assert.deepEqual(stampOpacity(0), { confirm: 0, reject: 0 });
    assert.deepEqual(stampOpacity(60), { confirm: 0.5, reject: 0 });
    assert.deepEqual(stampOpacity(120), { confirm: 1, reject: 0 });
    assert.deepEqual(stampOpacity(240), { confirm: 1, reject: 0 }); // clamped
  });

  it("ramps the reject stamp in with leftward drag", () => {
    assert.deepEqual(stampOpacity(-60), { confirm: 0, reject: 0.5 });
    assert.deepEqual(stampOpacity(-120), { confirm: 0, reject: 1 });
  });
});

describe("cardTransform", () => {
  it("translates and rotates proportional to the drag", () => {
    assert.equal(cardTransform(0), "translateX(0px) rotate(0deg)");
    assert.equal(cardTransform(80), "translateX(80px) rotate(2deg)");
    assert.equal(cardTransform(-40), "translateX(-40px) rotate(-1deg)");
  });
});

// ── nextBaseline / deckProgress — per-flyer high-water bar ───────────────────────

describe("nextBaseline", () => {
  it("is the high-water mark of deck length", () => {
    assert.equal(nextBaseline(0, 5), 5);
    assert.equal(nextBaseline(5, 3), 5); // shrinking deck does not lower the baseline
    assert.equal(nextBaseline(5, 7), 7); // a demotion enlarges it
    assert.equal(nextBaseline(undefined, 4), 4);
  });
});

describe("deckProgress", () => {
  it("reports N left and the fill percent against the baseline", () => {
    assert.deepEqual(deckProgress(5, 5), { left: 5, percent: 0 });
    assert.deepEqual(deckProgress(3, 5), { left: 3, percent: 40 });
    assert.deepEqual(deckProgress(0, 5), { left: 0, percent: 100 });
  });

  it("is 0% when the baseline is zero (nothing seeded)", () => {
    assert.deepEqual(deckProgress(0, 0), { left: 0, percent: 0 });
  });
});

// ── escapeHtml — untrusted flyer strings ────────────────────────────────────────

describe("escapeHtml", () => {
  it("escapes the five HTML-significant characters", () => {
    assert.equal(
      escapeHtml(`<b>"Bob's" & <Co></b>`),
      "&lt;b&gt;&quot;Bob&#39;s&quot; &amp; &lt;Co&gt;&lt;/b&gt;",
    );
  });

  it("coerces null/undefined to an empty string", () => {
    assert.equal(escapeHtml(null), "");
    assert.equal(escapeHtml(undefined), "");
  });

  it("leaves a plain flyer name untouched", () => {
    assert.equal(escapeHtml("Breyers Ice Cream"), "Breyers Ice Cream");
  });
});
