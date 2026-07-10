// @ts-check
//
// Unit tests for intake-review-logic.js (ADR-020, bead plantry-2zvm.11).
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
  isUnmatched,
  buildSaveLineBody,
  commitBarCounts,
  estimateHint,
  hasCompletePrefill,
  isReadyPending,
  decisionVariant,
  questionCopy,
  firstNeedsLineId,
} from "../intake-review-logic.js";

// ── test helpers ─────────────────────────────────────────────────────────────

/**
 * Minimal signal stub — a plain object with a `value` property.
 * The logic functions only read `.value`; they never call signal-specific
 * methods like `.subscribe` or `.peek`.
 *
 * @template T
 * @param {T} v
 * @returns {{ value: T }}
 */
function sig(v) {
  return { value: v };
}

/**
 * Build a minimal LineSeed with sensible defaults. Individual tests override
 * only the fields that matter for that case.
 *
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
 * Build a minimal PrefillData with sensible defaults.
 *
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
 * Build a complete LineState from seed + prefill using the stub signal factory.
 * Shorthand so test bodies stay focused.
 *
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
    assert.equal(body.skuId, null);          // prefill.skuId is null → null
    assert.equal(body.newProductName, null);
    assert.equal(body.newProductCategoryId, null);
    assert.equal(body.quantity, 1);
    assert.equal(body.unitId, "unit-L");
    assert.equal(body.locationId, "loc-fridge");
    assert.equal(body.expiryDate, null);      // draftExpiryMode is "never"
    assert.equal(body.price, 3.99);
  });

  it("parseFloat of a valid string quantity", () => {
    const ls = makeState({}, { quantity: 2.5 });
    const body = buildSaveLineBody(ls);
    assert.equal(body.quantity, 2.5);
  });

  it("parseFloat of garbage qty yields NaN (caller must validate before calling)", () => {
    const ls = makeState();
    ls.draftQty.value = "abc";
    const body = buildSaveLineBody(ls);
    assert.ok(Number.isNaN(body.quantity), "expected NaN for garbage qty");
  });

  it("parseFloat of empty-string qty yields NaN", () => {
    const ls = makeState();
    ls.draftQty.value = "";
    const body = buildSaveLineBody(ls);
    assert.ok(Number.isNaN(body.quantity), "expected NaN for empty qty string");
  });

  it("createNew branch: nulls productId and skuId, populates newProductName and category", () => {
    const ls = makeState(
      { isNewProduct: true, newProductName: "Oat Milk", newProductCategoryId: "cat-dairy" },
      { productId: null, productName: null, skuId: null },
    );
    ls.createNew.value = true;
    ls.draftNewName.value = "  Oat Milk  "; // should be trimmed
    ls.draftNewCategoryId.value = "cat-dairy";
    ls.draftProductId.value = "";            // even if something leaked in, it must be ignored
    ls.draftSkuId.value = "sku-xyz";

    const body = buildSaveLineBody(ls);

    assert.equal(body.createNew, true);
    assert.equal(body.productId, null, "productId must be null in createNew branch");
    assert.equal(body.skuId, null, "skuId must be null in createNew branch");
    assert.equal(body.newProductName, "Oat Milk", "newProductName must be trimmed");
    assert.equal(body.newProductCategoryId, "cat-dairy");
  });

  it("createNew branch: newProductCategoryId uses (value || null) — empty string becomes null", () => {
    const ls = makeState({ isNewProduct: true }, { productId: null });
    ls.createNew.value = true;
    ls.draftNewName.value = "Coconut Yoghurt";
    ls.draftNewCategoryId.value = "";

    const body = buildSaveLineBody(ls);

    assert.equal(body.newProductCategoryId, null);
  });

  it("existing-product branch: productId uses (value || null) — empty string becomes null", () => {
    const ls = makeState();
    ls.createNew.value = false;
    ls.draftProductId.value = "";

    const body = buildSaveLineBody(ls);

    assert.equal(body.productId, null);
    assert.equal(body.newProductName, null);
  });

  it("existing-product branch: skuId uses (value || null) — empty string becomes null", () => {
    const ls = makeState();
    ls.createNew.value = false;
    ls.draftSkuId.value = "";

    const body = buildSaveLineBody(ls);

    assert.equal(body.skuId, null);
  });

  it("expiryDate is set when draftExpiryMode is 'has' and draftExpiry is non-empty", () => {
    const ls = makeState();
    ls.draftExpiryMode.value = "has";
    ls.draftExpiry.value = "2026-12-31";

    const body = buildSaveLineBody(ls);

    assert.equal(body.expiryDate, "2026-12-31");
  });

  it("expiryDate is null when draftExpiryMode is 'has' but draftExpiry is empty", () => {
    const ls = makeState();
    ls.draftExpiryMode.value = "has";
    ls.draftExpiry.value = "";

    const body = buildSaveLineBody(ls);

    assert.equal(body.expiryDate, null);
  });

  it("expiryDate is null when draftExpiryMode is 'never' even if draftExpiry has a value", () => {
    const ls = makeState();
    ls.draftExpiryMode.value = "never";
    ls.draftExpiry.value = "2026-12-31";

    const body = buildSaveLineBody(ls);

    assert.equal(body.expiryDate, null);
  });

  it("price is null when draftPrice is empty", () => {
    const ls = makeState();
    ls.draftPrice.value = "";

    const body = buildSaveLineBody(ls);

    assert.equal(body.price, null);
  });

  it("price is parseFloat when draftPrice is a valid number string", () => {
    const ls = makeState();
    ls.draftPrice.value = "5.49";

    const body = buildSaveLineBody(ls);

    assert.equal(body.price, 5.49);
  });

  it("unitId uses (value || null) — empty string becomes null", () => {
    const ls = makeState();
    ls.draftUnitId.value = "";

    const body = buildSaveLineBody(ls);

    assert.equal(body.unitId, null);
  });

  it("locationId uses (value || null) — empty string becomes null", () => {
    const ls = makeState();
    ls.draftLocationId.value = "";

    const body = buildSaveLineBody(ls);

    assert.equal(body.locationId, null);
  });
});

// ── makeLine ─────────────────────────────────────────────────────────────────

describe("makeLine", () => {
  describe("drawerOpen (exceptions-first: focus drives the question drawer, not this flag)", () => {
    it("always starts closed — the focused exception's drawer is opened by the island's focusId", () => {
      for (const seed of [
        { status: "Pending", confidence: "Low", productId: null, isNewProduct: false },
        { status: "Pending", confidence: "None", productId: null, isNewProduct: false },
        { status: "Pending", confidence: "High", productId: "prod-abc", isNewProduct: false },
        { status: "Confirmed", confidence: "High", productId: "prod-abc", isNewProduct: false },
        { status: "Dismissed", confidence: "None", productId: null, isNewProduct: false },
      ]) {
        const ls = makeState(seed);
        assert.equal(ls.drawerOpen.value, false, `expected closed for ${JSON.stringify(seed)}`);
      }
    });
  });

  describe("draft coercions", () => {
    it("draftQty is String(prefill.quantity) when non-null", () => {
      const ls = makeState({}, { quantity: 3 });
      assert.equal(ls.draftQty.value, "3");
    });

    it("draftQty is empty string when prefill.quantity is null", () => {
      const ls = makeState({}, { quantity: null });
      assert.equal(ls.draftQty.value, "");
    });

    it("draftExpiryMode is 'has' when prefill.expiry is non-null", () => {
      const ls = makeState({}, { expiry: "2026-06-30" });
      assert.equal(ls.draftExpiryMode.value, "has");
      assert.equal(ls.draftExpiry.value, "2026-06-30");
    });

    it("draftExpiryMode is 'never' when prefill.expiry is null", () => {
      const ls = makeState({}, { expiry: null });
      assert.equal(ls.draftExpiryMode.value, "never");
      assert.equal(ls.draftExpiry.value, "");
    });

    it("draftPrice is String(prefill.price) when non-null", () => {
      const ls = makeState({}, { price: 2.49 });
      assert.equal(ls.draftPrice.value, "2.49");
    });

    it("draftPrice is empty string when prefill.price is null", () => {
      const ls = makeState({}, { price: null });
      assert.equal(ls.draftPrice.value, "");
    });

    it("draftProductId falls back to empty string when prefill.productId is null", () => {
      const ls = makeState({}, { productId: null });
      assert.equal(ls.draftProductId.value, "");
    });

    it("draftSkuId falls back to empty string when prefill.skuId is null", () => {
      const ls = makeState({}, { skuId: null });
      assert.equal(ls.draftSkuId.value, "");
    });
  });

  describe("price signal", () => {
    it("uses line.price when set (price wins over suggestedPrice)", () => {
      const ls = makeState({ price: 4.99, suggestedPrice: 3.99 });
      assert.equal(ls.price.value, 4.99);
    });

    it("falls back to suggestedPrice when line.price is null", () => {
      const ls = makeState({ price: null, suggestedPrice: 3.99 });
      assert.equal(ls.price.value, 3.99);
    });

    it("is null when both line.price and suggestedPrice are null", () => {
      const ls = makeState({ price: null, suggestedPrice: null });
      assert.equal(ls.price.value, null);
    });
  });

  it("createNew starts true when line.isNewProduct is true", () => {
    const ls = makeState({ isNewProduct: true });
    assert.equal(ls.createNew.value, true);
  });

  it("createNew starts false when line.isNewProduct is false", () => {
    const ls = makeState({ isNewProduct: false });
    assert.equal(ls.createNew.value, false);
  });

  it("alternatives is null when seed alternatives is null", () => {
    const ls = makeLine(
      { line: lineSeed(), prefill: prefill(), alternatives: null },
      sig,
    );
    assert.equal(ls.alternatives, null);
  });

  it("alternatives is propagated when provided", () => {
    const alts = [{ productId: "p2", productName: "Skim Milk", confidence: 0.85 }];
    const ls = makeLine(
      { line: lineSeed(), prefill: prefill(), alternatives: alts },
      sig,
    );
    assert.deepEqual(ls.alternatives, alts);
  });
});

// ── lineSection ───────────────────────────────────────────────────────────────

describe("lineSection", () => {
  it("returns 'skipped' for Dismissed", () => {
    const ls = makeState({ status: "Dismissed" });
    assert.equal(lineSection(ls), "skipped");
  });

  it("returns 'ready' for Confirmed", () => {
    const ls = makeState({ status: "Confirmed" });
    assert.equal(lineSection(ls), "ready");
  });

  it("returns 'ready' for Committed", () => {
    const ls = makeState({ status: "Committed" });
    assert.equal(lineSection(ls), "ready");
  });

  it("returns 'ready' for a Pending High-confidence line with a complete prefill (auto-confirm at commit)", () => {
    // default makeState is High + complete prefill (product/qty/unit/location all set)
    const ls = makeState({ status: "Pending", confidence: "High" });
    assert.equal(lineSection(ls), "ready");
  });

  it("returns 'needs' for a Pending High-confidence line with an INCOMPLETE prefill (missing location)", () => {
    const ls = makeState({ status: "Pending", confidence: "High" }, { locationId: null });
    assert.equal(lineSection(ls), "needs");
  });

  it("returns 'needs' for a Pending Low-confidence line even with a complete prefill", () => {
    const ls = makeState({ status: "Pending", confidence: "Low" });
    assert.equal(lineSection(ls), "needs");
  });

  it("returns 'needs' for a Pending None-confidence unmatched line", () => {
    const ls = makeState(
      { status: "Pending", confidence: "None", productId: null },
      { productId: null },
    );
    assert.equal(lineSection(ls), "needs");
  });
});

// ── hasCompletePrefill / isReadyPending ─────────────────────────────────────────

describe("hasCompletePrefill", () => {
  it("true when product, qty>0, unit and location are all present", () => {
    assert.equal(hasCompletePrefill(makeState()), true);
  });

  it("false when product is missing", () => {
    assert.equal(hasCompletePrefill(makeState({}, { productId: null })), false);
  });

  it("false when location is missing", () => {
    assert.equal(hasCompletePrefill(makeState({}, { locationId: null })), false);
  });

  it("false when unit is missing", () => {
    assert.equal(hasCompletePrefill(makeState({}, { unitId: null })), false);
  });

  it("false when quantity is zero or absent", () => {
    assert.equal(hasCompletePrefill(makeState({}, { quantity: 0 })), false);
    assert.equal(hasCompletePrefill(makeState({}, { quantity: null })), false);
  });
});

describe("isReadyPending", () => {
  it("true only for Pending + High + complete prefill", () => {
    assert.equal(isReadyPending(makeState({ status: "Pending", confidence: "High" })), true);
  });

  it("false for Low confidence", () => {
    assert.equal(isReadyPending(makeState({ status: "Pending", confidence: "Low" })), false);
  });

  it("false for High but incomplete prefill", () => {
    assert.equal(isReadyPending(makeState({ status: "Pending", confidence: "High" }, { unitId: null })), false);
  });

  it("false for non-Pending statuses", () => {
    for (const status of ["Confirmed", "Dismissed", "Committed"]) {
      assert.equal(isReadyPending(makeState({ status, confidence: "High" })), false, status);
    }
  });
});

// ── decisionVariant ─────────────────────────────────────────────────────────────

describe("decisionVariant", () => {
  it("'create' when createNew is set", () => {
    const ls = makeState();
    ls.createNew.value = true;
    assert.equal(decisionVariant(ls, 0), "create");
  });

  it("'create' when there is no product and no alternatives", () => {
    const ls = makeState({ productId: null }, { productId: null });
    assert.equal(decisionVariant(ls, 0), "create");
  });

  it("'estimate' when the line carries a weight→each estimate", () => {
    const ls = makeLine(
      { line: lineSeed(), prefill: prefill(), alternatives: null,
        estimate: { eachCount: 7, weight: 1.34, weightUnit: "lb", confidence: "High" } },
      sig,
    );
    assert.equal(decisionVariant(ls, 0), "estimate");
  });

  it("'match' when 2+ alternatives are present (and no estimate)", () => {
    const alts = [
      { productId: "p1", productName: "A", confidence: 0.8 },
      { productId: "p2", productName: "B", confidence: 0.5 },
    ];
    const ls = makeLine({ line: lineSeed(), prefill: prefill(), alternatives: alts }, sig);
    assert.equal(decisionVariant(ls, 0), "match");
  });

  it("'sku' when the drafted product has 2+ pack sizes (no estimate, <2 alternatives)", () => {
    const ls = makeState();
    assert.equal(decisionVariant(ls, 2), "sku");
  });

  it("'fields' as the fallback for a matched line with a single sku", () => {
    const ls = makeState();
    assert.equal(decisionVariant(ls, 1), "fields");
  });
});

// ── questionCopy ─────────────────────────────────────────────────────────────────

describe("questionCopy", () => {
  it("returns a distinct non-empty question + why for every variant", () => {
    const seen = new Set();
    for (const v of ["create", "estimate", "match", "sku", "fields"]) {
      const c = questionCopy(/** @type {any} */ (v));
      assert.ok(c.question && c.why, `variant ${v} must have copy`);
      assert.ok(!seen.has(c.question), `variant ${v} question must be distinct`);
      seen.add(c.question);
    }
  });

  it("falls back to the generic copy for an unknown variant", () => {
    assert.deepEqual(questionCopy(/** @type {any} */ ("nope")), questionCopy("fields"));
  });
});

// ── firstNeedsLineId ─────────────────────────────────────────────────────────────

describe("firstNeedsLineId", () => {
  it("returns the lineId of the first line still in the needs section", () => {
    const ready = makeState({ lineId: "a", status: "Confirmed" });
    const needs1 = makeState({ lineId: "b", status: "Pending", confidence: "Low" });
    const needs2 = makeState({ lineId: "c", status: "Pending", confidence: "Low" });
    assert.equal(firstNeedsLineId([ready, needs1, needs2]), "b");
  });

  it("returns null when nothing needs review", () => {
    const ready = makeState({ lineId: "a", status: "Confirmed" });
    const skipped = makeState({ lineId: "b", status: "Dismissed" });
    assert.equal(firstNeedsLineId([ready, skipped]), null);
  });
});

// ── isUnmatched ───────────────────────────────────────────────────────────────

describe("isUnmatched", () => {
  it("returns true for Pending + Low confidence", () => {
    const ls = makeState({ status: "Pending", confidence: "Low" });
    assert.equal(isUnmatched(ls), true);
  });

  it("returns true for Pending + None confidence", () => {
    const ls = makeState({ status: "Pending", confidence: "None" });
    assert.equal(isUnmatched(ls), true);
  });

  it("returns false for Pending + High confidence (matched row)", () => {
    const ls = makeState({ status: "Pending", confidence: "High" });
    assert.equal(isUnmatched(ls), false);
  });

  it("returns false for Confirmed even if confidence is Low", () => {
    const ls = makeState({ status: "Confirmed", confidence: "Low" });
    assert.equal(isUnmatched(ls), false);
  });

  it("returns false for Dismissed", () => {
    const ls = makeState({ status: "Dismissed", confidence: "None" });
    assert.equal(isUnmatched(ls), false);
  });

  it("returns false for Committed", () => {
    const ls = makeState({ status: "Committed", confidence: "Low" });
    assert.equal(isUnmatched(ls), false);
  });
});

// ── commitBarCounts ─────────────────────────────────────────────────────────

describe("commitBarCounts", () => {
  it("counts each section and totalItems = needs + ready (skipped excluded)", () => {
    const r = commitBarCounts(["needs", "ready", "ready", "skipped"]);
    assert.equal(r.needsCount, 1);
    assert.equal(r.readyCount, 2);
    assert.equal(r.skippedCount, 1);
    assert.equal(r.totalItems, 3);
  });

  it("remaining equals needsCount — the gate and the displayed count share one primitive", () => {
    for (const sections of [
      [],
      ["needs"],
      ["needs", "needs", "ready"],
      ["ready", "ready", "skipped"],
      ["needs", "ready", "skipped", "needs"],
    ]) {
      const r = commitBarCounts(sections);
      assert.equal(r.remaining, r.needsCount, JSON.stringify(sections));
    }
  });

  it("canCommit only when nothing needs resolving AND there is something to commit", () => {
    assert.equal(commitBarCounts(["ready", "ready"]).canCommit, true);
    assert.equal(commitBarCounts(["needs", "ready"]).canCommit, false);
    // all skipped → nothing to commit
    assert.equal(commitBarCounts(["skipped", "skipped"]).canCommit, false);
    // empty → nothing to commit
    assert.equal(commitBarCounts([]).canCommit, false);
  });

  it("progressPct is ready/total, and 100 when there is nothing to do", () => {
    assert.equal(commitBarCounts(["ready", "ready", "needs", "needs"]).progressPct, 50);
    assert.equal(commitBarCounts(["ready", "ready", "ready"]).progressPct, 100);
    assert.equal(commitBarCounts(["skipped"]).progressPct, 100); // totalItems 0
    assert.equal(commitBarCounts([]).progressPct, 100);
  });

  it("a skipped line never counts toward remaining or blocks commit", () => {
    const r = commitBarCounts(["ready", "skipped", "skipped"]);
    assert.equal(r.remaining, 0);
    assert.equal(r.canCommit, true);
  });
});

// ── estimateHint (plantry-1mu) ─────────────────────────────────────────────────

describe("estimateHint", () => {
  it("returns null when there is no estimate", () => {
    assert.equal(estimateHint(null), null);
    assert.equal(estimateHint(undefined ?? null), null);
  });

  it("high confidence phrases the each-count as a provenance note", () => {
    const hint = estimateHint({ eachCount: 7, weight: 1.34, weightUnit: "lb", confidence: "High" });
    assert.equal(hint, "~7 each · estimated from 1.34 lb");
  });

  it("low confidence phrases it as a soft weight-first suggestion", () => {
    const hint = estimateHint({ eachCount: 6, weight: 0.9, weightUnit: "kg", confidence: "Low" });
    assert.equal(hint, "Sold by weight (0.9 kg) · ~6 each?");
  });
});

describe("makeLine estimate passthrough", () => {
  it("carries the estimate object onto line state, defaulting to null", () => {
    const est = { eachCount: 7, weight: 1.34, weightUnit: "lb", confidence: "High" };
    const withEst = makeLine(
      { line: lineSeed(), prefill: /** @type {any} */ ({}), alternatives: null, estimate: est },
      sig,
    );
    assert.deepEqual(withEst.estimate, est);

    const withoutEst = makeLine(
      { line: lineSeed(), prefill: /** @type {any} */ ({}), alternatives: null },
      sig,
    );
    assert.equal(withoutEst.estimate, null);
  });
});
