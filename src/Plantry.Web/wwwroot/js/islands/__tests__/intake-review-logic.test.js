// @ts-check
//
// Unit tests for intake-review-logic.js (ADR-020, bead plantry-2zvm.11 / deck-flow rewrite plantry-gpdb).
//
// Run with: node --test  (from repo root)
// Or:       npm test
//
// No npm dependencies — uses Node's built-in test runner and assert module.
// Imports the island logic module directly as ESM; no browser globals needed
// (the logic functions are pure transforms of their arguments).

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import {
  makeLine,
  lineSection,
  isSurePending,
  isPrefillComplete,
  buildSaveLineBody,
  commitBarCounts,
  estimateHint,
  decisionVariant,
  deckReasoning,
  optionRankLabel,
  demotedDecision,
  railLineView,
  reconciliation,
  filterStores,
  buildCorrectHeaderBody,
  // reused deck primitives (deal-deck-logic.js) — covered here for intake's use of them
  buildDeckOrder,
  applySkip,
  applyBack,
  reconcileSkipStack,
} from "../intake-review-logic.js";

// ── test helpers ─────────────────────────────────────────────────────────────

/**
 * Minimal signal stub — a plain object with a `value` property.
 * @template T @param {T} v @returns {{ value: T }}
 */
function sig(v) {
  return { value: v };
}

/**
 * @param {Partial<import("../intake-review-logic.js").LineSeed>} overrides
 * @returns {import("../intake-review-logic.js").LineSeed}
 */
function lineSeed(overrides = {}) {
  return {
    lineId: "line-1",
    receiptText: "Whole Milk 2L",
    confidence: "High",
    status: "Pending",
    productId: "prod-abc",
    skuId: null,
    quantity: 1,
    unitId: "unit-L",
    locationId: "loc-fridge",
    expiryDate: null,
    price: 3.99,
    isNewProduct: false,
    newProductName: null,
    newProductCategoryId: null,
    suggestedPrice: 3.99,
    ...overrides,
  };
}

/**
 * @param {Partial<import("../intake-review-logic.js").PrefillData>} overrides
 * @returns {import("../intake-review-logic.js").PrefillData}
 */
function prefill(overrides = {}) {
  return {
    productId: "prod-abc",
    productName: "Whole Milk 2L",
    quantity: 1,
    unitId: "unit-L",
    locationId: "loc-fridge",
    price: 3.99,
    expiry: null,
    skuId: null,
    ...overrides,
  };
}

/**
 * @param {Partial<import("../intake-review-logic.js").LineSeed>} lineOverrides
 * @param {Partial<import("../intake-review-logic.js").PrefillData>} prefillOverrides
 * @returns {import("../intake-review-logic.js").LineState}
 */
function makeState(lineOverrides = {}, prefillOverrides = {}) {
  return makeLine(
    { line: lineSeed(lineOverrides), prefill: prefill(prefillOverrides), alternatives: null },
    sig,
  );
}

// ── buildSaveLineBody ─────────────────────────────────────────────────────────

describe("buildSaveLineBody", () => {
  it("assembles a well-formed body for an existing-product line", () => {
    const ls = makeState();
    const body = buildSaveLineBody(ls);
    assert.equal(body.lineId, "line-1");
    assert.equal(body.createNew, false);
    assert.equal(body.productId, "prod-abc");
    assert.equal(body.skuId, null);
    assert.equal(body.newProductName, null);
    assert.equal(body.newProductCategoryId, null);
    assert.equal(body.quantity, 1);
    assert.equal(body.unitId, "unit-L");
    assert.equal(body.locationId, "loc-fridge");
    assert.equal(body.expiryDate, null);
    assert.equal(body.price, 3.99);
  });

  it("parseFloat of garbage / empty qty yields NaN (caller must validate before calling)", () => {
    const ls = makeState();
    ls.draftQty.value = "abc";
    assert.ok(Number.isNaN(buildSaveLineBody(ls).quantity));
    ls.draftQty.value = "";
    assert.ok(Number.isNaN(buildSaveLineBody(ls).quantity));
  });

  it("createNew branch: nulls productId/skuId, trims name, (value || null) for category", () => {
    const ls = makeState(
      { isNewProduct: true, newProductName: "Oat Milk", newProductCategoryId: "cat-dairy" },
      { productId: null, productName: null },
    );
    ls.createNew.value = true;
    ls.draftNewName.value = "  Oat Milk  ";
    ls.draftNewCategoryId.value = "cat-dairy";
    ls.draftProductId.value = "leaked";
    ls.draftSkuId.value = "sku-xyz";
    const body = buildSaveLineBody(ls);
    assert.equal(body.createNew, true);
    assert.equal(body.productId, null);
    assert.equal(body.skuId, null);
    assert.equal(body.newProductName, "Oat Milk");
    assert.equal(body.newProductCategoryId, "cat-dairy");

    ls.draftNewCategoryId.value = "";
    assert.equal(buildSaveLineBody(ls).newProductCategoryId, null);
  });

  it("existing-product branch: empty productId/skuId/unit/location become null", () => {
    const ls = makeState();
    ls.createNew.value = false;
    ls.draftProductId.value = "";
    ls.draftSkuId.value = "";
    ls.draftUnitId.value = "";
    ls.draftLocationId.value = "";
    const body = buildSaveLineBody(ls);
    assert.equal(body.productId, null);
    assert.equal(body.skuId, null);
    assert.equal(body.unitId, null);
    assert.equal(body.locationId, null);
    assert.equal(body.newProductName, null);
  });

  it("expiryDate honours the has/never mode", () => {
    const ls = makeState();
    ls.draftExpiryMode.value = "has";
    ls.draftExpiry.value = "2026-12-31";
    assert.equal(buildSaveLineBody(ls).expiryDate, "2026-12-31");
    ls.draftExpiry.value = "";
    assert.equal(buildSaveLineBody(ls).expiryDate, null);
    ls.draftExpiryMode.value = "never";
    ls.draftExpiry.value = "2026-12-31";
    assert.equal(buildSaveLineBody(ls).expiryDate, null);
  });

  it("price is parseFloat when set, null when empty", () => {
    const ls = makeState();
    ls.draftPrice.value = "5.49";
    assert.equal(buildSaveLineBody(ls).price, 5.49);
    ls.draftPrice.value = "";
    assert.equal(buildSaveLineBody(ls).price, null);
  });
});

// ── makeLine ─────────────────────────────────────────────────────────────────

describe("makeLine", () => {
  it("checked starts true (pre-checked in the sure-things checklist) and demoted starts false", () => {
    const ls = makeState();
    assert.equal(ls.checked.value, true);
    assert.equal(ls.demoted.value, false);
  });

  it("aiComplete snapshots the ORIGINAL prefill completeness (frozen from the seed, not the drafts)", () => {
    assert.equal(makeState().aiComplete, true);
    assert.equal(makeState({}, { unitId: null }).aiComplete, false);
    assert.equal(makeState({}, { productId: null }).aiComplete, false);
    assert.equal(makeState({}, { locationId: null }).aiComplete, false);
    assert.equal(makeState({}, { quantity: 0 }).aiComplete, false);
    assert.equal(makeState({}, { quantity: null }).aiComplete, false);
  });

  it("drawerOpen always starts closed (the confirmed-row edit drawer is user-toggled)", () => {
    for (const seed of [
      { status: "Pending", confidence: "High" },
      { status: "Confirmed", confidence: "High" },
      { status: "Dismissed", confidence: "None" },
    ]) {
      assert.equal(makeState(seed).drawerOpen.value, false, JSON.stringify(seed));
    }
  });

  describe("draft coercions", () => {
    it("draftQty is String(prefill.quantity) or '' when null", () => {
      assert.equal(makeState({}, { quantity: 3 }).draftQty.value, "3");
      assert.equal(makeState({}, { quantity: null }).draftQty.value, "");
    });

    it("draftExpiryMode reflects prefill.expiry presence", () => {
      const withExp = makeState({}, { expiry: "2026-06-30" });
      assert.equal(withExp.draftExpiryMode.value, "has");
      assert.equal(withExp.draftExpiry.value, "2026-06-30");
      const noExp = makeState({}, { expiry: null });
      assert.equal(noExp.draftExpiryMode.value, "never");
      assert.equal(noExp.draftExpiry.value, "");
    });

    it("draftProductId / draftSkuId fall back to empty string", () => {
      assert.equal(makeState({}, { productId: null }).draftProductId.value, "");
      assert.equal(makeState({}, { skuId: null }).draftSkuId.value, "");
    });
  });

  describe("price signal", () => {
    it("line.price wins over suggestedPrice; falls back to suggestedPrice; null when both null", () => {
      assert.equal(makeState({ price: 4.99, suggestedPrice: 3.99 }).price.value, 4.99);
      assert.equal(makeState({ price: null, suggestedPrice: 3.99 }).price.value, 3.99);
      assert.equal(makeState({ price: null, suggestedPrice: null }).price.value, null);
    });
  });

  it("createNew starts from line.isNewProduct", () => {
    assert.equal(makeState({ isNewProduct: true }).createNew.value, true);
    assert.equal(makeState({ isNewProduct: false }).createNew.value, false);
  });

  it("alternatives passthrough (null by default, propagated when provided)", () => {
    assert.equal(makeState().alternatives, null);
    const alts = [{ productId: "p2", productName: "Skim Milk", confidence: 0.85 }];
    const ls = makeLine({ line: lineSeed(), prefill: prefill(), alternatives: alts }, sig);
    assert.deepEqual(ls.alternatives, alts);
  });
});

// ── lineSection (four-way: confirmed / sure / needs / skipped) ────────────────────

describe("lineSection", () => {
  it("returns 'skipped' for Dismissed", () => {
    assert.equal(lineSection(makeState({ status: "Dismissed" })), "skipped");
  });

  it("returns 'confirmed' for Confirmed and Committed", () => {
    assert.equal(lineSection(makeState({ status: "Confirmed" })), "confirmed");
    assert.equal(lineSection(makeState({ status: "Committed" })), "confirmed");
  });

  it("returns 'sure' for a Pending High-confidence line with a complete prefill", () => {
    assert.equal(lineSection(makeState({ status: "Pending", confidence: "High" })), "sure");
  });

  it("returns 'needs' for a sure line the user demoted (unchecked into the deck)", () => {
    const ls = makeState({ status: "Pending", confidence: "High" });
    ls.demoted.value = true;
    assert.equal(lineSection(ls), "needs");
  });

  it("returns 'needs' for a Pending High line with an incomplete prefill (missing location)", () => {
    assert.equal(lineSection(makeState({ status: "Pending", confidence: "High" }, { locationId: null })), "needs");
  });

  it("returns 'needs' for Pending Low/None confidence lines", () => {
    assert.equal(lineSection(makeState({ status: "Pending", confidence: "Low" })), "needs");
    assert.equal(lineSection(makeState({ status: "Pending", confidence: "None", productId: null }, { productId: null })), "needs");
  });
});

// ── isPrefillComplete / isSurePending (mirror of the ConfirmLines predicate) ─────

describe("isPrefillComplete", () => {
  it("true when product, qty>0, unit and location are all present", () => {
    assert.equal(isPrefillComplete("prod-abc", 1, "unit-L", "loc-fridge"), true);
  });

  it("false when any of product / unit / location is missing or qty is not > 0", () => {
    assert.equal(isPrefillComplete(null, 1, "unit-L", "loc-fridge"), false);
    assert.equal(isPrefillComplete("prod-abc", 1, null, "loc-fridge"), false);
    assert.equal(isPrefillComplete("prod-abc", 1, "unit-L", null), false);
    assert.equal(isPrefillComplete("prod-abc", 0, "unit-L", "loc-fridge"), false);
    assert.equal(isPrefillComplete("prod-abc", null, "unit-L", "loc-fridge"), false);
    assert.equal(isPrefillComplete("prod-abc", NaN, "unit-L", "loc-fridge"), false);
  });
});

describe("isSurePending", () => {
  it("true only for Pending + High + AI-complete prefill + not demoted", () => {
    assert.equal(isSurePending(makeState({ status: "Pending", confidence: "High" })), true);
  });

  it("false when demoted (the user pushed it to the deck)", () => {
    const ls = makeState({ status: "Pending", confidence: "High" });
    ls.demoted.value = true;
    assert.equal(isSurePending(ls), false);
  });

  it("false for Low confidence, AI-incomplete prefill, or non-Pending status", () => {
    assert.equal(isSurePending(makeState({ status: "Pending", confidence: "Low" })), false);
    assert.equal(isSurePending(makeState({ status: "Pending", confidence: "High" }, { unitId: null })), false);
    for (const status of ["Confirmed", "Dismissed", "Committed"]) {
      assert.equal(isSurePending(makeState({ status, confidence: "High" })), false, status);
    }
  });

  it("stays false when the AI prefill was incomplete but the user's edits later complete the drafts (plantry-wv4h)", () => {
    // A High-confidence line the AI left incomplete (no unit) belongs in the editable deck, not the checklist.
    const ls = makeState({ status: "Pending", confidence: "High" }, { unitId: null });
    assert.equal(isSurePending(ls), false);
    // The user manually picks the unit in the deck, completing the LIVE draft prefill …
    ls.draftUnitId.value = "unit-L";
    assert.equal(
      isPrefillComplete(
        ls.draftProductId.value,
        parseFloat(ls.draftQty.value),
        ls.draftUnitId.value,
        ls.draftLocationId.value,
      ),
      true,
    );
    // … but it must NOT be promoted into the (non-editable) sure checklist: bulk-confirm re-derives from
    // the untouched AI suggestion (still incomplete) and would reject it. It stays editable in the deck.
    assert.equal(isSurePending(ls), false);
  });

  it("stays sure regardless of later draft edits when the AI prefill WAS complete (snapshot is immutable)", () => {
    const ls = makeState({ status: "Pending", confidence: "High" });
    assert.equal(isSurePending(ls), true);
    // Even blanking a live draft field does not un-sure it — aiComplete is frozen at hydration.
    ls.draftUnitId.value = "";
    assert.equal(
      isPrefillComplete(
        ls.draftProductId.value,
        parseFloat(ls.draftQty.value),
        ls.draftUnitId.value,
        ls.draftLocationId.value,
      ),
      false,
    );
    assert.equal(isSurePending(ls), true);
  });
});

// ── decisionVariant / deckReasoning / optionRankLabel ────────────────────────────

describe("decisionVariant", () => {
  it("'create' when createNew is set or there is no product and no alternatives", () => {
    const cn = makeState();
    cn.createNew.value = true;
    assert.equal(decisionVariant(cn, 0), "create");
    assert.equal(decisionVariant(makeState({ productId: null }, { productId: null }), 0), "create");
  });

  it("'estimate' when the line carries a weight→each estimate", () => {
    const ls = makeLine(
      { line: lineSeed(), prefill: prefill(), alternatives: null,
        estimate: { eachCount: 7, weight: 1.34, weightUnit: "lb", confidence: "High" } },
      sig,
    );
    assert.equal(decisionVariant(ls, 0), "estimate");
  });

  it("'match' when 2+ alternatives are present (no estimate)", () => {
    const alts = [
      { productId: "p1", productName: "A", confidence: 0.8 },
      { productId: "p2", productName: "B", confidence: 0.5 },
    ];
    assert.equal(decisionVariant(makeLine({ line: lineSeed(), prefill: prefill(), alternatives: alts }, sig), 0), "match");
  });

  it("'sku' when the drafted product has 2+ pack sizes; 'fields' as the fallback", () => {
    assert.equal(decisionVariant(makeState(), 2), "sku");
    assert.equal(decisionVariant(makeState(), 1), "fields");
  });
});

describe("deckReasoning", () => {
  it("returns a distinct non-empty statement for every variant", () => {
    const seen = new Set();
    for (const v of ["create", "estimate", "match", "sku", "fields"]) {
      const r = deckReasoning(/** @type {any} */ (v));
      assert.ok(r && typeof r === "string", `variant ${v} must have copy`);
      assert.ok(!seen.has(r), `variant ${v} must be distinct`);
      seen.add(r);
    }
  });

  it("falls back to the generic (fields) copy for an unknown variant", () => {
    assert.equal(deckReasoning(/** @type {any} */ ("nope")), deckReasoning("fields"));
  });
});

describe("optionRankLabel", () => {
  it("the recommended top pick reads 'best'", () => {
    assert.equal(optionRankLabel(0, 0.78, true), "best");
  });

  it("non-top options read their confidence as a percentage", () => {
    assert.equal(optionRankLabel(1, 0.44, false), "44%");
    assert.equal(optionRankLabel(2, 0.31, false), "31%");
  });

  it("a zero-confidence option (escape hatch) shows nothing", () => {
    assert.equal(optionRankLabel(2, 0, false), "");
  });

  it("a non-recommended index-0 falls through to its percentage", () => {
    assert.equal(optionRankLabel(0, 0.5, false), "50%");
  });
});

// ── demotedDecision (uncheck-demote synthesis) ───────────────────────────────────

describe("demotedDecision", () => {
  it("synthesises a single-option 'double-check the match' decision from the current match", () => {
    const d = demotedDecision("Greek yogurt (plain)", "prod-gy", 0.96);
    assert.match(d.reasoning, /double-check the match/i);
    assert.equal(d.option.label, "Greek yogurt (plain)");
    assert.equal(d.option.productId, "prod-gy");
    assert.equal(d.option.confidence, 0.96);
    assert.equal(d.option.recommended, true);
  });

  it("defaults confidence to 0 and tolerates a null product id", () => {
    const d = demotedDecision("Mystery", null);
    assert.equal(d.option.confidence, 0);
    assert.equal(d.option.productId, null);
  });
});

// ── commitBarCounts (deck-flow: sure + needs both block commit) ──────────────────

describe("commitBarCounts", () => {
  it("counts each pool; totalItems = confirmed + sure + needs (skipped excluded)", () => {
    const r = commitBarCounts(["confirmed", "sure", "needs", "skipped"]);
    assert.equal(r.confirmedCount, 1);
    assert.equal(r.sureCount, 1);
    assert.equal(r.needsCount, 1);
    assert.equal(r.skippedCount, 1);
    assert.equal(r.totalItems, 3);
  });

  it("remaining = sure + needs — the gate and the displayed count share one primitive", () => {
    for (const sections of [
      [],
      ["sure"],
      ["needs", "sure", "confirmed"],
      ["confirmed", "confirmed", "skipped"],
      ["sure", "needs", "skipped", "confirmed"],
    ]) {
      const r = commitBarCounts(/** @type {any} */ (sections));
      assert.equal(r.remaining, r.sureCount + r.needsCount, JSON.stringify(sections));
      assert.equal(r.unresolved, r.remaining);
    }
  });

  it("canCommit only when nothing is unresolved AND something is confirmed", () => {
    assert.equal(commitBarCounts(["confirmed", "confirmed"]).canCommit, true);
    assert.equal(commitBarCounts(["sure", "confirmed"]).canCommit, false);   // a sure thing still blocks
    assert.equal(commitBarCounts(["needs", "confirmed"]).canCommit, false);  // a deck card still blocks
    assert.equal(commitBarCounts(["skipped", "skipped"]).canCommit, false);  // nothing to commit
    assert.equal(commitBarCounts([]).canCommit, false);
  });

  it("progressPct = (confirmed + skipped) / all lines, 100 when there is nothing", () => {
    assert.equal(commitBarCounts(["confirmed", "confirmed", "sure", "needs"]).progressPct, 50);
    assert.equal(commitBarCounts(["confirmed", "skipped"]).progressPct, 100);
    assert.equal(commitBarCounts([]).progressPct, 100);
  });

  it("a skipped line never counts toward remaining or blocks commit", () => {
    const r = commitBarCounts(["confirmed", "skipped", "skipped"]);
    assert.equal(r.remaining, 0);
    assert.equal(r.canCommit, true);
  });
});

// ── estimateHint (plantry-1mu) ─────────────────────────────────────────────────

describe("estimateHint", () => {
  it("returns null when there is no estimate", () => {
    assert.equal(estimateHint(null), null);
  });

  it("high confidence phrases the each-count as provenance; low confidence as a soft suggestion", () => {
    assert.equal(
      estimateHint({ eachCount: 7, weight: 1.34, weightUnit: "lb", confidence: "High" }),
      "~7 each · estimated from 1.34 lb",
    );
    assert.equal(
      estimateHint({ eachCount: 6, weight: 0.9, weightUnit: "kg", confidence: "Low" }),
      "Sold by weight (0.9 kg) · ~6 each?",
    );
  });
});

// ── railLineView (receipt minimap glyph state, four-way) ─────────────────────────

describe("railLineView", () => {
  it("a confirmed line is done + tick", () => {
    const v = railLineView("confirmed", false);
    assert.deepEqual({ done: v.done, dim: v.dim, glyph: v.glyph }, { done: true, dim: false, glyph: "tick" });
  });

  it("a needs line pulses a dot", () => {
    const v = railLineView("needs", false);
    assert.deepEqual({ done: v.done, dim: v.dim, glyph: v.glyph }, { done: false, dim: false, glyph: "dot" });
  });

  it("a sure line shows no glyph, is neither done nor dim (an unconfirmed sure thing)", () => {
    const v = railLineView("sure", false);
    assert.deepEqual({ done: v.done, dim: v.dim, glyph: v.glyph }, { done: false, dim: false, glyph: null });
  });

  it("a skipped line dims with no glyph", () => {
    const v = railLineView("skipped", false);
    assert.deepEqual({ done: v.done, dim: v.dim, glyph: v.glyph }, { done: false, dim: true, glyph: null });
  });

  it("the active (deck top) line is active regardless of section", () => {
    assert.equal(railLineView("needs", true).active, true);
    assert.equal(railLineView("confirmed", true).active, true);
    assert.equal(railLineView("needs", false).active, false);
  });
});

// ── reconciliation (receipt money footer, deck buckets) ──────────────────────────

describe("reconciliation", () => {
  /** 2 confirmed (pantry) + 1 sure + 1 needs (both undecided) + 1 skipped fee. */
  const items = [
    { section: /** @type {const} */ ("confirmed"), price: 10.0 },
    { section: /** @type {const} */ ("confirmed"), price: 5.5 },
    { section: /** @type {const} */ ("sure"), price: 2.0 },
    { section: /** @type {const} */ ("needs"), price: 2.0 },
    { section: /** @type {const} */ ("skipped"), price: 0.1 },
  ];

  it("pantry = confirmed; undecided = sure + needs; fees = skipped", () => {
    const r = reconciliation(items, 1.4, 21.0);
    assert.equal(r.pantry, 15.5);
    assert.equal(r.undecided, 4.0);
    assert.equal(r.skippedFees, 0.1);
  });

  it("reconciles (✓) only when parts + tax add up to the total within a cent", () => {
    assert.equal(reconciliation(items, 1.4, 21.0).reconciles, true);
    assert.equal(reconciliation(items, 1.4, 25.0).reconciles, false);
  });

  it("degrades a null/undefined/NaN total or tax to null — never NaN", () => {
    assert.equal(reconciliation(items, 1.4, null).total, null);
    assert.equal(reconciliation(items, 1.4, NaN).total, null);
    assert.equal(reconciliation(items, null, 21.0).tax, null);
    assert.equal(reconciliation(items, NaN, 21.0).tax, null);
    assert.equal(reconciliation(items, 1.4, null).reconciles, false);
  });

  it("treats a null line price as zero and rounds float drift to cents", () => {
    const drift = [
      { section: /** @type {const} */ ("confirmed"), price: 0.1 },
      { section: /** @type {const} */ ("confirmed"), price: 0.2 },
      { section: /** @type {const} */ ("confirmed"), price: null },
    ];
    assert.equal(reconciliation(drift, null, null).pantry, 0.3);
  });

  it("an empty session yields all-zero sums and null totals", () => {
    const r = reconciliation([], null, null);
    assert.deepEqual(
      { pantry: r.pantry, undecided: r.undecided, skippedFees: r.skippedFees, tax: r.tax, total: r.total, reconciles: r.reconciles },
      { pantry: 0, undecided: 0, skippedFees: 0, tax: null, total: null, reconciles: false },
    );
  });
});

// ── reused deck primitives — intake's use of deal-deck-logic (acceptance #1) ─────

describe("deck order + skip-stack rotation (reused deal-deck-logic primitives)", () => {
  it("buildDeckOrder keeps prior skip-rotation order and appends newly-eligible ids", () => {
    // needs pool = [a, b, c]; the deck previously had them rotated to [b, c, a] via a skip.
    assert.deepEqual(buildDeckOrder(["a", "b", "c"], ["b", "c", "a"]), ["b", "c", "a"]);
    // a resolved card (a) drops out of the needs pool; a demoted card (d) is appended at the end.
    assert.deepEqual(buildDeckOrder(["b", "c", "d"], ["b", "c", "a"]), ["b", "c", "d"]);
  });

  it("applySkip rotates the top card to the back and records it for undo", () => {
    const r = applySkip(["a", "b", "c"], []);
    assert.deepEqual(r.order, ["b", "c", "a"]);
    assert.deepEqual(r.skipStack, ["a"]);
  });

  it("applyBack restores the last-skipped card to the front", () => {
    const r = applyBack(["b", "c", "a"], ["a"]);
    assert.deepEqual(r.order, ["a", "b", "c"]);
    assert.deepEqual(r.skipStack, []);
  });

  it("reconcileSkipStack drops ids no longer in the deck (skipped then resolved elsewhere)", () => {
    assert.deepEqual(reconcileSkipStack(["a", "gone"], ["a", "b"]), ["a"]);
  });
});

// ── review header correction (plantry-yobz) ──────────────────────────────────────

describe("filterStores", () => {
  const stores = [
    { id: "1", name: "Food Basics" },
    { id: "2", name: "Metro" },
    { id: "3", name: "FreshCo" },
    { id: "4", name: "No Frills" },
  ];

  it("returns all stores (capped) for a blank query", () => {
    assert.deepEqual(filterStores(stores, "").map((s) => s.id), ["1", "2", "3", "4"]);
    assert.deepEqual(filterStores(stores, "   ").map((s) => s.id), ["1", "2", "3", "4"]);
  });

  it("filters case-insensitively by substring", () => {
    assert.deepEqual(filterStores(stores, "fre").map((s) => s.name), ["FreshCo"]);
    assert.deepEqual(filterStores(stores, "o").map((s) => s.name), ["Food Basics", "Metro", "FreshCo", "No Frills"]);
  });

  it("caps results at the limit", () => {
    assert.equal(filterStores(stores, "", 2).length, 2);
  });

  it("returns [] when nothing matches", () => {
    assert.deepEqual(filterStores(stores, "zzz"), []);
  });
});

describe("buildCorrectHeaderBody", () => {
  it("passes through a full header, trimming the merchant", () => {
    assert.deepEqual(
      buildCorrectHeaderBody({ merchantText: "  Food Basics  ", selectedStoreId: "s1", purchaseDate: "2026-07-19", purchaseTime: "17:05" }),
      { merchantText: "Food Basics", selectedStoreId: "s1", purchaseDate: "2026-07-19", purchaseTime: "17:05" });
  });

  it("maps every blank field to null so the server clears it", () => {
    assert.deepEqual(
      buildCorrectHeaderBody({ merchantText: "   ", selectedStoreId: "", purchaseDate: "", purchaseTime: "" }),
      { merchantText: null, selectedStoreId: null, purchaseDate: null, purchaseTime: null });
  });

  it("a typed merchant with no store id is the create-new path (id null, name kept)", () => {
    assert.deepEqual(
      buildCorrectHeaderBody({ merchantText: "Corner Store", selectedStoreId: "", purchaseDate: "", purchaseTime: "" }),
      { merchantText: "Corner Store", selectedStoreId: null, purchaseDate: null, purchaseTime: null });
  });

  it("tolerates undefined fields", () => {
    assert.deepEqual(
      buildCorrectHeaderBody({}),
      { merchantText: null, selectedStoreId: null, purchaseDate: null, purchaseTime: null });
  });
});
