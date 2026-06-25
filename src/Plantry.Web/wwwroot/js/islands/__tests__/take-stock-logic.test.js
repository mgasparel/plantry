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
