// @ts-check
//
// Unit tests for take-stock-logic.js (ADR-020, bead plantry-2zvm.13).
//
// Run with: node --test  (from repo root)
// Or:       npm test
//
// No npm dependencies — uses Node's built-in test runner and assert module.
// Imports the island logic module directly as ESM; no browser globals needed
// (the logic functions are pure transforms of their arguments).
//
// save() itself is NOT tested here: it calls postJson (network I/O) and is inseparable
// from the fetch lifecycle. Its pure sub-steps — buildSaveItems, reconcileResults, and
// saveStatusMessage (the status/toast wording) — are tested below.
// Full save behaviour is covered by the Playwright E2E suite.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import {
  setCount,
  makeRow,
  buildSaveItems,
  reconcileResults,
  saveStatusMessage,
  mergeSheetUnitIntoRow,
  shouldShowMarkCounted,
  rowStatus,
  toggleRowCheck,
  confirmRow,
  groupRowsByCategory,
  readyToSaveCount,
} from "../take-stock-logic.js";

// Import the vendored reactive runtime so dirty/down computed tests exercise
// the real reactive graph rather than a snapshot-at-construction stub.
// This import is zero-deps — the vendor module is already committed alongside
// the island; no npm install needed.
import { signal, computed } from "../vendor/signals.module.js";

// ── test helpers ─────────────────────────────────────────────────────────────

/**
 * Minimal signal stub — a plain object with a writable `value` property.
 * The logic functions only read/write `.value`; they never call signal-specific
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
 * Minimal computed stub — calls the thunk immediately and wraps the result in
 * a signal-like object. Because the test stubs are plain mutable objects (not
 * reactive), the "computed" value is a one-time snapshot taken at construction
 * time. Tests that verify dirty/down behaviour mutate counted/recorded directly
 * and then check the computed logic separately, or construct fresh rows with the
 * final values already set.
 *
 * This mirrors how plantry-2zvm.11 injected `signal` — extended here for `computed`.
 *
 * @template T
 * @param {() => T} fn
 * @returns {{ value: T }}
 */
function comp(fn) {
  return { value: fn() };
}

/**
 * Build a minimal RowSeed with sensible defaults. Individual tests override
 * only the fields that matter for that case.
 *
 * @param {Partial<import("../take-stock-logic.js").RowSeed>} overrides
 * @returns {import("../take-stock-logic.js").RowSeed}
 */
function rowSeed(overrides = {}) {
  return {
    productId: "prod-milk",
    productName: "Whole Milk 2L",
    recorded: 3,
    unitCode: "L",
    unitId: "unit-l",
    hasActiveStock: true,
    lotsUrl: "/pantry/take-stock/walk/loc-1?handler=Lots&productId=prod-milk",
    supportedUnits: [{ unitId: "unit-l", code: "L" }],
    isNewRow: false,
    ...overrides,
  };
}

/**
 * Build a Row using the stub signal/computed factories.
 * Use for tests that only need static snapshot reads (defaults, seed propagation,
 * setCount, buildSaveItems, reconcileResults) where reactivity is not required.
 *
 * @param {Partial<import("../take-stock-logic.js").RowSeed>} overrides
 * @returns {import("../take-stock-logic.js").Row}
 */
function makeTestRow(overrides = {}) {
  return makeRow(rowSeed(overrides), sig, comp);
}

/**
 * Build a Row using the REAL reactive signal/computed from the vendored Preact
 * signals runtime. Use for dirty/down computed tests where mutations to
 * counted/recorded must propagate reactively through the computed graph.
 *
 * This is the same runtime the browser island uses — the tests exercise the
 * actual dependency graph, not a snapshot of the formula at construction time.
 *
 * @param {Partial<import("../take-stock-logic.js").RowSeed>} overrides
 * @returns {import("../take-stock-logic.js").Row}
 */
function makeRealRow(overrides = {}) {
  return makeRow(rowSeed(overrides), signal, computed);
}

// ── setCount ─────────────────────────────────────────────────────────────────

describe("setCount", () => {
  it("numeric passthrough: sets counted to the given number", () => {
    const row = makeTestRow({ recorded: 3 });
    setCount(row, 5);
    assert.equal(row.counted.value, 5);
  });

  it("valid string: parses float and sets counted", () => {
    const row = makeTestRow({ recorded: 3 });
    setCount(row, "2.5");
    assert.equal(row.counted.value, 2.5);
  });

  it("empty string: NaN falls back to row.recorded.value", () => {
    const row = makeTestRow({ recorded: 3 });
    setCount(row, "");
    assert.equal(row.counted.value, 3);
  });

  it("garbage string ('abc'): NaN falls back to row.recorded.value", () => {
    const row = makeTestRow({ recorded: 7 });
    setCount(row, "abc");
    assert.equal(row.counted.value, 7);
  });

  it("negative value clamps to 0", () => {
    const row = makeTestRow({ recorded: 3 });
    setCount(row, -1);
    assert.equal(row.counted.value, 0);
  });

  it("negative string clamps to 0", () => {
    const row = makeTestRow({ recorded: 3 });
    setCount(row, "-5");
    assert.equal(row.counted.value, 0);
  });

  it("zero is a valid value (not treated as falsy/NaN)", () => {
    const row = makeTestRow({ recorded: 3 });
    setCount(row, 0);
    assert.equal(row.counted.value, 0);
  });

  it("resets failed to false", () => {
    const row = makeTestRow();
    row.failed.value = true;
    setCount(row, 2);
    assert.equal(row.failed.value, false);
  });

  it("resets failMsg to null", () => {
    const row = makeTestRow();
    row.failMsg.value = "Something went wrong";
    setCount(row, 2);
    assert.equal(row.failMsg.value, null);
  });

  it("resets failed even when the input is NaN (fallback path)", () => {
    const row = makeTestRow({ recorded: 3 });
    row.failed.value = true;
    row.failMsg.value = "Prior error";
    setCount(row, "not-a-number");
    assert.equal(row.counted.value, 3, "should fall back to recorded");
    assert.equal(row.failed.value, false, "failed should be cleared");
    assert.equal(row.failMsg.value, null, "failMsg should be cleared");
  });
});

// ── makeRow ───────────────────────────────────────────────────────────────────

describe("makeRow", () => {
  describe("dirty computed — uses real reactive signals so mutations propagate", () => {
    it("is false at initial state (counted === recorded)", () => {
      // makeRealRow injects the vendor signal/computed — dirty is a live computed.
      const row = makeRealRow({ recorded: 5 });
      assert.equal(row.dirty.value, false);
    });

    it("becomes true after counted is mutated to a different value", () => {
      const row = makeRealRow({ recorded: 5 });
      row.counted.value = 3;
      // Reading row.dirty.value re-evaluates the reactive computed expression
      // (counted !== recorded) from makeRow line 145. A bug like `===` or `>`
      // in that expression would be caught here.
      assert.equal(row.dirty.value, true);
    });

    it("returns to false when counted is reset back to recorded", () => {
      const row = makeRealRow({ recorded: 5 });
      row.counted.value = 3;
      assert.equal(row.dirty.value, true, "precondition: dirty after change");
      row.counted.value = 5; // restore to recorded
      assert.equal(row.dirty.value, false);
    });

    it("becomes true when counted increases above recorded", () => {
      const row = makeRealRow({ recorded: 5 });
      row.counted.value = 8;
      assert.equal(row.dirty.value, true);
    });
  });

  describe("down computed — uses real reactive signals so mutations propagate", () => {
    it("is false at initial state (counted === recorded, not dirty)", () => {
      const row = makeRealRow({ recorded: 5 });
      assert.equal(row.down.value, false);
    });

    it("becomes true when counted decreases below recorded", () => {
      const row = makeRealRow({ recorded: 5 });
      row.counted.value = 3;
      // row.down = dirty && counted < recorded — reads through the reactive graph.
      // A bug like `>` instead of `<` in makeRow line 146 would be caught here.
      assert.equal(row.dirty.value, true,  "precondition: dirty");
      assert.equal(row.down.value,  true,  "decrease → down");
    });

    it("is false when counted increases above recorded (dirty but NOT down)", () => {
      const row = makeRealRow({ recorded: 5 });
      row.counted.value = 8;
      assert.equal(row.dirty.value, true,  "precondition: dirty");
      assert.equal(row.down.value,  false, "increase → not down");
    });

    it("returns to false when counted is restored to recorded", () => {
      const row = makeRealRow({ recorded: 5 });
      row.counted.value = 3;
      assert.equal(row.down.value, true, "precondition: down while decreased");
      row.counted.value = 5;
      assert.equal(row.down.value, false);
    });

    it("is false when both counted and recorded are 0 (not dirty)", () => {
      const row = makeRealRow({ recorded: 0 });
      // counted starts at 0 = recorded → dirty=false → down=false
      assert.equal(row.dirty.value, false);
      assert.equal(row.down.value,  false);
    });
  });

  describe("defaults", () => {
    it("reason defaults to 'Correction'", () => {
      const row = makeTestRow();
      assert.equal(row.reason.value, "Correction");
    });

    it("failed defaults to false", () => {
      const row = makeTestRow();
      assert.equal(row.failed.value, false);
    });

    it("failMsg defaults to null", () => {
      const row = makeTestRow();
      assert.equal(row.failMsg.value, null);
    });

    it("hasActiveStock falls back to false when undefined in seed", () => {
      const row = makeTestRow({ hasActiveStock: undefined });
      assert.equal(row.hasActiveStock, false);
    });

    it("lotsUrl falls back to empty string when undefined in seed", () => {
      const row = makeTestRow({ lotsUrl: undefined });
      assert.equal(row.lotsUrl, "");
    });

    it("supportedUnits falls back to empty array when undefined in seed", () => {
      const row = makeTestRow({ supportedUnits: undefined });
      assert.deepEqual(row.supportedUnits, []);
    });

    it("isNewRow falls back to false when undefined in seed", () => {
      const row = makeTestRow({ isNewRow: undefined });
      assert.equal(row.isNewRow, false);
    });

    it("isNewRow is set to true when seed.isNewRow is true", () => {
      const row = makeTestRow({ isNewRow: true });
      assert.equal(row.isNewRow, true);
    });

    it("needsConversion defaults to false with empty conversion fields (plantry-3mwx)", () => {
      const row = makeTestRow();
      assert.equal(row.needsConversion.value, false);
      assert.equal(row.convFromUnitId.value, "");
      assert.equal(row.convFromCode.value, "");
      assert.equal(row.convToUnitId.value, "");
      assert.equal(row.convToCode.value, "");
      assert.equal(row.convFactor.value, "");
    });

    it("expiryDate defaults to empty string when absent in seed (plantry-4onl)", () => {
      const row = makeTestRow();
      assert.equal(row.expiryDate.value, "");
    });

    it("expiryDate is seeded from seed.expiryDate when present (plantry-4onl)", () => {
      const row = makeTestRow({ expiryDate: "2027-05-20" });
      assert.equal(row.expiryDate.value, "2027-05-20");
    });

    it("confirmed defaults to false (plantry-vvqt — an untouched row is 'todo')", () => {
      const row = makeTestRow();
      assert.equal(row.confirmed.value, false);
    });

    it("saveLotsUrl falls back to empty string when undefined in seed (plantry-vvqt)", () => {
      const row = makeTestRow({ saveLotsUrl: undefined });
      assert.equal(row.saveLotsUrl, "");
    });

    it("categoryName falls back to null when undefined in seed (plantry-vvqt)", () => {
      const row = makeTestRow({ categoryName: undefined });
      assert.equal(row.categoryName, null);
    });

    it("categorySortOrder falls back to Number.MAX_SAFE_INTEGER when undefined in seed (plantry-vvqt)", () => {
      const row = makeTestRow({ categorySortOrder: undefined });
      assert.equal(row.categorySortOrder, Number.MAX_SAFE_INTEGER);
    });
  });

  describe("seed values propagated", () => {
    it("productId comes from seed", () => {
      const row = makeTestRow({ productId: "prod-abc" });
      assert.equal(row.productId, "prod-abc");
    });

    it("productName comes from seed", () => {
      const row = makeTestRow({ productName: "Oat Milk" });
      assert.equal(row.productName, "Oat Milk");
    });

    it("unitCode comes from seed", () => {
      const row = makeTestRow({ unitCode: "kg" });
      assert.equal(row.unitCode, "kg");
    });

    it("unitId signal starts at seed.unitId", () => {
      const row = makeTestRow({ unitId: "unit-kg" });
      assert.equal(row.unitId.value, "unit-kg");
    });

    it("recorded signal starts at seed.recorded", () => {
      const row = makeTestRow({ recorded: 7 });
      assert.equal(row.recorded.value, 7);
    });

    it("counted signal starts at seed.recorded (pre-filled to recorded)", () => {
      const row = makeTestRow({ recorded: 7 });
      assert.equal(row.counted.value, 7);
    });

    it("hasActiveStock comes from seed when provided", () => {
      const row = makeTestRow({ hasActiveStock: true });
      assert.equal(row.hasActiveStock, true);
    });

    it("lotsUrl comes from seed when provided", () => {
      const url = "/pantry/take-stock/walk/loc-9?handler=Lots&productId=prod-x";
      const row = makeTestRow({ lotsUrl: url });
      assert.equal(row.lotsUrl, url);
    });

    it("supportedUnits comes from seed when provided", () => {
      const units = [{ unitId: "unit-l", code: "L" }, { unitId: "unit-ml", code: "mL" }];
      const row = makeTestRow({ supportedUnits: units });
      assert.deepEqual(row.supportedUnits, units);
    });
  });
});

// ── buildSaveItems ────────────────────────────────────────────────────────────

describe("buildSaveItems", () => {
  it("maps a single dirty row to the correct item shape", () => {
    const row = makeTestRow({ productId: "prod-a" });
    row.counted.value = 5;
    row.unitId.value = "unit-l";
    row.reason.value = "Consumed";

    const items = buildSaveItems([row]);

    assert.equal(items.length, 1);
    assert.equal(items[0].productId, "prod-a");
    assert.equal(items[0].countedValue, 5);
    assert.equal(items[0].countedUnitId, "unit-l");
    assert.equal(items[0].reason, "Consumed");
  });

  it("maps multiple dirty rows", () => {
    const rowA = makeTestRow({ productId: "prod-a" });
    rowA.counted.value = 2;
    rowA.unitId.value = "unit-l";
    rowA.reason.value = "Correction";

    const rowB = makeTestRow({ productId: "prod-b", unitId: "unit-kg" });
    rowB.counted.value = 0.5;
    rowB.reason.value = "Discarded";

    const items = buildSaveItems([rowA, rowB]);

    assert.equal(items.length, 2);
    assert.equal(items[0].productId, "prod-a");
    assert.equal(items[0].countedValue, 2);
    assert.equal(items[1].productId, "prod-b");
    assert.equal(items[1].countedValue, 0.5);
    assert.equal(items[1].reason, "Discarded");
  });

  it("returns empty array for empty input", () => {
    const items = buildSaveItems([]);
    assert.deepEqual(items, []);
  });

  it("uses the default reason ('Correction') when unmodified", () => {
    const row = makeTestRow({ productId: "prod-c" });
    const items = buildSaveItems([row]);
    assert.equal(items[0].reason, "Correction");
  });

  // ── expiryDate (plantry-4onl) — uses makeRealRow so the reactive `down` computed
  // actually reflects the counted/recorded relationship (the plain stub snapshots
  // `down` once at construction, before the test's mutations).
  it("includes expiryDate on an increase (counted > recorded) row", () => {
    const row = makeRealRow({ productId: "prod-up", recorded: 3 });
    row.counted.value = 5;
    row.expiryDate.value = "2027-01-15";

    const items = buildSaveItems([row]);

    assert.equal(items[0].expiryDate, "2027-01-15");
  });

  it("omits expiryDate on a decrease (counted < recorded) row, even when set", () => {
    const row = makeRealRow({ productId: "prod-down", recorded: 5 });
    row.counted.value = 2;
    row.expiryDate.value = "2027-01-15";

    const items = buildSaveItems([row]);

    assert.equal("expiryDate" in items[0], false);
  });

  it("omits expiryDate on an increase row when the field was left blank", () => {
    const row = makeRealRow({ productId: "prod-up-blank", recorded: 3 });
    row.counted.value = 5;

    const items = buildSaveItems([row]);

    assert.equal("expiryDate" in items[0], false);
  });
});

// ── reconcileResults ─────────────────────────────────────────────────────────

describe("reconcileResults", () => {
  it("advances recorded to counted and clears failed/failMsg on success", () => {
    const row = makeTestRow({ recorded: 5, productId: "prod-a" });
    row.counted.value = 3;

    reconcileResults([row], [{ productId: "prod-a", isSuccess: true, error: null }]);

    assert.equal(row.recorded.value, 3, "recorded should advance to counted");
    assert.equal(row.failed.value, false);
    assert.equal(row.failMsg.value, null);
  });

  it("marks the row confirmed on success (plantry-vvqt — a saved row has been reviewed)", () => {
    const row = makeTestRow({ recorded: 5, productId: "prod-a" });
    row.counted.value = 3;
    assert.equal(row.confirmed.value, false, "precondition: not yet confirmed");

    reconcileResults([row], [{ productId: "prod-a", isSuccess: true, error: null }]);

    assert.equal(row.confirmed.value, true);
  });

  it("sets failed=true and failMsg from result.error on failure", () => {
    const row = makeTestRow({ productId: "prod-b" });

    reconcileResults([row], [{ productId: "prod-b", isSuccess: false, error: "Lot mismatch" }]);

    assert.equal(row.failed.value, true);
    assert.equal(row.failMsg.value, "Lot mismatch");
  });

  it("falls back to 'Failed to save' when result.error is null", () => {
    const row = makeTestRow({ productId: "prod-c" });

    reconcileResults([row], [{ productId: "prod-c", isSuccess: false, error: null }]);

    assert.equal(row.failMsg.value, "Failed to save");
  });

  it("returns correct saved/failed counts", () => {
    const rowA = makeTestRow({ productId: "prod-a" });
    const rowB = makeTestRow({ productId: "prod-b" });
    const rowC = makeTestRow({ productId: "prod-c" });

    const { saved, failed } = reconcileResults([rowA, rowB, rowC], [
      { productId: "prod-a", isSuccess: true, error: null },
      { productId: "prod-b", isSuccess: false, error: "Error" },
      { productId: "prod-c", isSuccess: true, error: null },
    ]);

    assert.equal(saved, 2);
    assert.equal(failed, 1);
  });

  it("ignores results for unknown productIds", () => {
    const row = makeTestRow({ productId: "prod-known" });
    const initialRecorded = row.recorded.value;

    const { saved, failed } = reconcileResults([row], [
      { productId: "prod-unknown", isSuccess: true, error: null },
    ]);

    // The known row should be untouched
    assert.equal(row.recorded.value, initialRecorded);
    assert.equal(saved, 0);
    assert.equal(failed, 0);
  });

  it("returns { saved: 0, failed: 0 } for empty results array", () => {
    const row = makeTestRow({ productId: "prod-a" });
    const { saved, failed } = reconcileResults([row], []);
    assert.equal(saved, 0);
    assert.equal(failed, 0);
  });

  it("successful result clears a previously-failed row's error state", () => {
    const row = makeTestRow({ productId: "prod-a" });
    row.failed.value = true;
    row.failMsg.value = "Previous network error";

    reconcileResults([row], [{ productId: "prod-a", isSuccess: true, error: null }]);

    assert.equal(row.failed.value, false);
    assert.equal(row.failMsg.value, null);
  });

  it("all-success batch: saved equals result count, failed is 0", () => {
    const rows = [
      makeTestRow({ productId: "prod-1" }),
      makeTestRow({ productId: "prod-2" }),
    ];
    const { saved, failed } = reconcileResults(rows, [
      { productId: "prod-1", isSuccess: true, error: null },
      { productId: "prod-2", isSuccess: true, error: null },
    ]);
    assert.equal(saved, 2);
    assert.equal(failed, 0);
  });

  it("all-failure batch: saved is 0, failed equals result count", () => {
    const rows = [
      makeTestRow({ productId: "prod-1" }),
      makeTestRow({ productId: "prod-2" }),
    ];
    const { saved, failed } = reconcileResults(rows, [
      { productId: "prod-1", isSuccess: false, error: "E1" },
      { productId: "prod-2", isSuccess: false, error: "E2" },
    ]);
    assert.equal(saved, 0);
    assert.equal(failed, 2);
  });

  // ── NeedsConversion rows (plantry-3mwx) ──────────────────────────────────────

  it("needsConversion result puts the row into the prompt state, not failed", () => {
    const row = makeTestRow({ productId: "prod-a", unitId: "unit-cup" });

    const { saved, failed, needsConversion } = reconcileResults([row], [{
      productId: "prod-a",
      isSuccess: false,
      needsConversion: true,
      fromUnitId: "unit-cup",
      fromUnitCode: "cup",
      toUnitId: "unit-g",
      toUnitCode: "g",
      error: "This unit needs a conversion factor before it can be recorded.",
    }]);

    assert.equal(row.needsConversion.value, true);
    assert.equal(row.convFromUnitId.value, "unit-cup");
    assert.equal(row.convFromCode.value, "cup");
    assert.equal(row.convToUnitId.value, "unit-g");
    assert.equal(row.convToCode.value, "g");
    assert.equal(row.failed.value, false, "needsConversion is not a plain failure");
    assert.equal(saved, 0);
    assert.equal(failed, 0);
    assert.equal(needsConversion, 1);
  });

  it("a later success clears a row's needsConversion prompt state", () => {
    const row = makeTestRow({ productId: "prod-a" });
    row.needsConversion.value = true;
    row.counted.value = 2;

    const { saved, needsConversion } = reconcileResults([row], [
      { productId: "prod-a", isSuccess: true, error: null },
    ]);

    assert.equal(row.needsConversion.value, false);
    assert.equal(row.recorded.value, 2);
    assert.equal(saved, 1);
    assert.equal(needsConversion, 0);
  });
});

// ── saveStatusMessage ─────────────────────────────────────────────────────────

describe("saveStatusMessage", () => {
  it("transport failure reports the status code", () => {
    assert.equal(
      saveStatusMessage({ ok: false, status: 503 }),
      "Save failed (503) — please try again",
    );
  });

  it("all saved: singular vs plural wording", () => {
    assert.equal(saveStatusMessage({ ok: true, saved: 1, failed: 0 }), "1 item updated");
    assert.equal(saveStatusMessage({ ok: true, saved: 3, failed: 0 }), "3 items updated");
  });

  it("all failed reports a retry message", () => {
    assert.equal(
      saveStatusMessage({ ok: true, saved: 0, failed: 2 }),
      "Save failed — please try again",
    );
  });

  it("partial success names both counts and points at the highlighted rows", () => {
    assert.equal(
      saveStatusMessage({ ok: true, saved: 2, failed: 1 }),
      "2 saved, 1 failed — retry the highlighted rows",
    );
  });

  it("ok with zero saved and zero failed (no results) is not a failure message", () => {
    // failed === 0 branch → "0 items updated" rather than the all-failed wording.
    assert.equal(saveStatusMessage({ ok: true, saved: 0, failed: 0 }), "0 items updated");
  });
});

// ── mergeSheetUnitIntoRow (plantry-1me7) ──────────────────────────────────────

describe("mergeSheetUnitIntoRow", () => {
  it("existing-row branch: carries the sheet-selected unit onto the row (the plantry-3mwx fix)", () => {
    // Row default unit is L (unit-l); the sheet chose an unconvertible unit "each" (unit-ea).
    const row = makeTestRow({ unitId: "unit-l", unitCode: "L", supportedUnits: [{ unitId: "unit-l", code: "L" }] });

    mergeSheetUnitIntoRow(row, { addCount: "6", addUnitId: "unit-ea", addUnitCode: "ea" });

    // The chosen unit is NOT dropped back to the product default — it is recorded on the row.
    assert.equal(row.unitId.value, "unit-ea");
    assert.equal(row.unitCode, "ea");
    assert.equal(row.counted.value, 6);
  });

  it("appends the chosen unit to supportedUnits when it is not already reachable", () => {
    const row = makeTestRow({ supportedUnits: [{ unitId: "unit-l", code: "L" }] });

    mergeSheetUnitIntoRow(row, { addCount: 2, addUnitId: "unit-ea", addUnitCode: "ea" });

    assert.deepEqual(row.supportedUnits, [
      { unitId: "unit-l", code: "L" },
      { unitId: "unit-ea", code: "ea" },
    ]);
  });

  it("does not duplicate a unit already present in supportedUnits", () => {
    const row = makeTestRow({
      supportedUnits: [{ unitId: "unit-l", code: "L" }, { unitId: "unit-ea", code: "ea" }],
    });

    mergeSheetUnitIntoRow(row, { addCount: 1, addUnitId: "unit-ea", addUnitCode: "ea" });

    assert.equal(row.supportedUnits.length, 2);
  });

  it("clears prior save-error and conversion-prompt state on the row", () => {
    const row = makeTestRow();
    row.failed.value = true;
    row.failMsg.value = "boom";
    row.needsConversion.value = true;

    mergeSheetUnitIntoRow(row, { addCount: "4", addUnitId: "unit-l", addUnitCode: "L" });

    assert.equal(row.failed.value, false);
    assert.equal(row.failMsg.value, null);
    assert.equal(row.needsConversion.value, false);
  });

  it("defaults a blank/absent count to 0", () => {
    const row = makeTestRow();
    mergeSheetUnitIntoRow(row, { addUnitId: "unit-l", addUnitCode: "L" });
    assert.equal(row.counted.value, 0);
  });

  // ── expiryDate carry (plantry-4onl) ──────────────────────────────────────
  it("carries the sheet-entered expiry onto the row", () => {
    const row = makeTestRow();
    mergeSheetUnitIntoRow(row, { addCount: 2, addUnitId: "unit-l", addUnitCode: "L", expiryDate: "2027-03-01" });
    assert.equal(row.expiryDate.value, "2027-03-01");
  });

  it("clears expiryDate to blank when the sheet field was left empty", () => {
    const row = makeTestRow();
    row.expiryDate.value = "2027-03-01"; // stale value from a prior merge
    mergeSheetUnitIntoRow(row, { addCount: 2, addUnitId: "unit-l", addUnitCode: "L" });
    assert.equal(row.expiryDate.value, "");
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// shouldShowMarkCounted (plantry-hp67)
// ─────────────────────────────────────────────────────────────────────────────

describe("shouldShowMarkCounted", () => {
  it("shows for a clean, non-empty, not-yet-marked walk", () => {
    assert.equal(shouldShowMarkCounted(0, 3, false), true);
  });

  it("hides while any row is dirty (a dirty walk completes via Save instead)", () => {
    assert.equal(shouldShowMarkCounted(2, 3, false), false);
  });

  it("hides for an empty location (nothing to count)", () => {
    assert.equal(shouldShowMarkCounted(0, 0, false), false);
  });

  it("hides once this session has already stamped the location", () => {
    assert.equal(shouldShowMarkCounted(0, 3, true), false);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// rowStatus / toggleRowCheck / confirmRow (plantry-vvqt walk redesign)
// ─────────────────────────────────────────────────────────────────────────────

describe("rowStatus", () => {
  it("is 'todo' for an untouched row (not dirty, not confirmed)", () => {
    const row = makeRealRow({ recorded: 5 });
    assert.equal(rowStatus(row), "todo");
  });

  it("is 'ok' once confirmed and not dirty", () => {
    const row = makeRealRow({ recorded: 5 });
    row.confirmed.value = true;
    assert.equal(rowStatus(row), "ok");
  });

  it("is 'chg' whenever dirty, regardless of confirmed", () => {
    const row = makeRealRow({ recorded: 5 });
    row.counted.value = 3;
    assert.equal(rowStatus(row), "chg", "dirty, not confirmed");
    row.confirmed.value = true;
    assert.equal(rowStatus(row), "chg", "dirty wins over confirmed");
  });
});

describe("toggleRowCheck", () => {
  it("todo → ok: confirms the row (counted already equals recorded)", () => {
    const row = makeRealRow({ recorded: 5 });
    toggleRowCheck(row);
    assert.equal(rowStatus(row), "ok");
    assert.equal(row.counted.value, 5);
  });

  it("ok → todo: un-confirms (a mis-tap escape hatch)", () => {
    const row = makeRealRow({ recorded: 5 });
    row.confirmed.value = true;
    toggleRowCheck(row);
    assert.equal(rowStatus(row), "todo");
  });

  it("chg → ok: re-confirms AT the recorded value, discarding the pending edit", () => {
    const row = makeRealRow({ recorded: 5 });
    row.counted.value = 2;
    assert.equal(rowStatus(row), "chg", "precondition");
    toggleRowCheck(row);
    assert.equal(row.counted.value, 5, "counted resets to recorded");
    assert.equal(rowStatus(row), "ok");
  });

  it("chg → ok: clears any prior failed/failMsg state", () => {
    const row = makeRealRow({ recorded: 5 });
    row.counted.value = 2;
    row.failed.value = true;
    row.failMsg.value = "boom";
    toggleRowCheck(row);
    assert.equal(row.failed.value, false);
    assert.equal(row.failMsg.value, null);
  });

  it("does NOT reset counted on a new (isNewRow) chg row — that would silently zero the addition", () => {
    // Inline-add rows are seeded recorded: 0 with counted set to the entered quantity
    // (take-stock.js handleSheetAdd) — resetting to recorded here would drop the addition.
    const row = makeRealRow({ recorded: 0, isNewRow: true });
    row.counted.value = 3;
    assert.equal(rowStatus(row), "chg", "precondition");

    toggleRowCheck(row);

    assert.equal(row.counted.value, 3, "counted must be left untouched for a new row");
    assert.equal(rowStatus(row), "chg", "dirty (counted !== recorded) still wins — status stays chg");
    assert.equal(row.confirmed.value, true);
  });
});

describe("confirmRow", () => {
  it("marks confirmed true without touching counted", () => {
    const row = makeRealRow({ recorded: 5 });
    row.counted.value = 8;
    confirmRow(row);
    assert.equal(row.confirmed.value, true);
    assert.equal(row.counted.value, 8, "counted is untouched — a dirty row stays dirty");
    assert.equal(rowStatus(row), "chg", "dirty still wins over confirmed");
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// groupRowsByCategory (plantry-vvqt design item 6)
// ─────────────────────────────────────────────────────────────────────────────

describe("groupRowsByCategory", () => {
  it("groups rows by categoryName", () => {
    const milk = makeTestRow({ productId: "milk", categoryName: "Dairy", categorySortOrder: 1 });
    const eggs = makeTestRow({ productId: "eggs", categoryName: "Dairy", categorySortOrder: 1 });
    const carrot = makeTestRow({ productId: "carrot", categoryName: "Produce", categorySortOrder: 2 });

    const groups = groupRowsByCategory([milk, eggs, carrot]);

    assert.equal(groups.length, 2);
    assert.deepEqual(groups.map((g) => g.name), ["Dairy", "Produce"]);
    assert.equal(groups[0].items.length, 2);
    assert.equal(groups[1].items.length, 1);
  });

  it("orders groups by categorySortOrder, not alphabetically", () => {
    const carrot = makeTestRow({ productId: "carrot", categoryName: "Produce", categorySortOrder: 2 });
    const milk = makeTestRow({ productId: "milk", categoryName: "Dairy", categorySortOrder: 5 });

    // Alphabetically Dairy < Produce, but sortOrder says Produce (2) comes before Dairy (5).
    const groups = groupRowsByCategory([milk, carrot]);

    assert.deepEqual(groups.map((g) => g.name), ["Produce", "Dairy"]);
  });

  it("buckets rows with no category under 'Other', sorted last", () => {
    const milk = makeTestRow({ productId: "milk", categoryName: "Dairy", categorySortOrder: 1 });
    const misc = makeTestRow({ productId: "misc", categoryName: null });

    const groups = groupRowsByCategory([misc, milk]);

    assert.deepEqual(groups.map((g) => g.name), ["Dairy", "Other"]);
  });

  it("keeps rows within a group in their incoming order (stable)", () => {
    const b = makeTestRow({ productId: "b", productName: "Banana", categoryName: "Produce" });
    const a = makeTestRow({ productId: "a", productName: "Apple", categoryName: "Produce" });

    const groups = groupRowsByCategory([b, a]);

    assert.deepEqual(groups[0].items.map((r) => r.productId), ["b", "a"]);
  });

  it("ties within the same sortOrder break alphabetically by category name", () => {
    const z = makeTestRow({ productId: "z", categoryName: "Zesty", categorySortOrder: 1 });
    const a = makeTestRow({ productId: "a", categoryName: "Ambient", categorySortOrder: 1 });

    const groups = groupRowsByCategory([z, a]);

    assert.deepEqual(groups.map((g) => g.name), ["Ambient", "Zesty"]);
  });

  it("returns an empty array for an empty row list", () => {
    assert.deepEqual(groupRowsByCategory([]), []);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// readyToSaveCount (plantry-vvqt design point 4 — lots ride the page-level Save)
// ─────────────────────────────────────────────────────────────────────────────

describe("readyToSaveCount", () => {
  it("is just the dirty row count when no lot panel is pending", () => {
    assert.equal(readyToSaveCount(2, {}), 2);
  });

  it("a lot-dirty product with NO dirty rows still yields a positive count", () => {
    // This is the case the adjuster sheet's Done button used to short-circuit (critic pass 1 FIX):
    // closing the sheet after only editing lot amounts (no row-level count change) must still
    // surface as a pending change once the sheet closes, not silently vanish.
    assert.equal(readyToSaveCount(0, { "prod-a": true }), 1);
  });

  it("sums dirty rows and dirty lot products together", () => {
    assert.equal(readyToSaveCount(2, { "prod-a": true, "prod-b": true }), 4);
  });

  it("is 0 when nothing is dirty", () => {
    assert.equal(readyToSaveCount(0, {}), 0);
  });
});
