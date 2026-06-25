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
    lineNo: 1,
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
    locationName: "Fridge",
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
  describe("drawerOpen predicate", () => {
    it("opens when Pending + Low confidence + no productId + not isNewProduct", () => {
      const ls = makeState({
        status: "Pending",
        confidence: "Low",
        productId: null,
        isNewProduct: false,
      });
      assert.equal(ls.drawerOpen.value, true);
    });

    it("opens when Pending + None confidence + no productId + not isNewProduct", () => {
      const ls = makeState({
        status: "Pending",
        confidence: "None",
        productId: null,
        isNewProduct: false,
      });
      assert.equal(ls.drawerOpen.value, true);
    });

    it("stays closed when confidence is High (matched row)", () => {
      const ls = makeState({
        status: "Pending",
        confidence: "High",
        productId: "prod-abc",
        isNewProduct: false,
      });
      assert.equal(ls.drawerOpen.value, false);
    });

    it("stays closed when productId is set even if confidence is Low", () => {
      const ls = makeState({
        status: "Pending",
        confidence: "Low",
        productId: "prod-abc", // already has a product
        isNewProduct: false,
      });
      assert.equal(ls.drawerOpen.value, false);
    });

    it("stays closed when isNewProduct is true even if confidence is Low and no productId", () => {
      const ls = makeState({
        status: "Pending",
        confidence: "Low",
        productId: null,
        isNewProduct: true,
      });
      assert.equal(ls.drawerOpen.value, false);
    });

    it("stays closed when status is not Pending", () => {
      for (const status of /** @type {const} */ (["Confirmed", "Dismissed", "Committed"])) {
        const ls = makeState({
          status,
          confidence: "Low",
          productId: null,
          isNewProduct: false,
        });
        assert.equal(ls.drawerOpen.value, false, `expected closed for status=${status}`);
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

  it("returns 'needs' for Pending", () => {
    const ls = makeState({ status: "Pending" });
    assert.equal(lineSection(ls), "needs");
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
