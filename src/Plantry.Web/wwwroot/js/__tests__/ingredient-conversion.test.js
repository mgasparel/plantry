// @ts-check
//
// Unit tests for ingredient-conversion.js (bead plantry-qno9).
//
// Run with: node --test  (from repo root)  or  npm test
// No npm dependencies — Node's built-in runner + assert, importing the ESM module directly.
//
// These pin the client-side echo math that translates the author's four-field equation
// ("leftAmount leftUnit = rightAmount rightUnit") into stock terms ("so 1 cup ≈ 125 g").

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import { stockPerRecipeUnit, formatEchoAmount } from "../ingredient-conversion.js";

// Nominal factor-to-base values used across the cases (mirror the seeded household units).
const G = 1;      // gram   — base of mass
const KG = 1000;  // kilogram
const CUP = 240;  // cup    — a volume factor-to-base
const TBSP = 15;  // tablespoon

describe("stockPerRecipeUnit — the derived echo (acceptance cases)", () => {
  it("AC4: '1 kg = 8 cup' for cups against grams → 1 cup ≈ 125 g", () => {
    const per = stockPerRecipeUnit({
      leftAmount: 1, rightAmount: 8,
      leftFactor: KG, rightFactor: CUP,
      recipeLineFactor: CUP, stockDefaultFactor: G,
    });
    assert.equal(per, 125);
    assert.equal(formatEchoAmount(per), "125");
  });

  it("simple one-number path '120 g = 1 cup' → 1 cup ≈ 120 g", () => {
    const per = stockPerRecipeUnit({
      leftAmount: 120, rightAmount: 1,
      leftFactor: G, rightFactor: CUP,
      recipeLineFactor: CUP, stockDefaultFactor: G,
    });
    assert.equal(per, 120);
  });

  it("bridges when the RIGHT unit differs from the recipe-line unit (equation in tbsp, line in cup)", () => {
    // "1 kg = 128 tbsp"; recipe line is cup (240 base), 1 cup = 16 tbsp → 1 cup ≈ 125 g.
    const per = stockPerRecipeUnit({
      leftAmount: 1, rightAmount: 128,
      leftFactor: KG, rightFactor: TBSP,
      recipeLineFactor: CUP, stockDefaultFactor: G,
    });
    assert.equal(per, 125);
  });

  it("accepts numeric strings (as posted from x-model number inputs)", () => {
    const per = stockPerRecipeUnit({
      leftAmount: "1", rightAmount: "8",
      leftFactor: "1000", rightFactor: "240",
      recipeLineFactor: "240", stockDefaultFactor: "1",
    });
    assert.equal(per, 125);
  });

  it("returns null while an amount is incomplete or non-positive (stay quiet mid-typing)", () => {
    const base = {
      leftFactor: KG, rightFactor: CUP, recipeLineFactor: CUP, stockDefaultFactor: G,
    };
    assert.equal(stockPerRecipeUnit({ ...base, leftAmount: "", rightAmount: 8 }), null);
    assert.equal(stockPerRecipeUnit({ ...base, leftAmount: 1, rightAmount: "" }), null);
    assert.equal(stockPerRecipeUnit({ ...base, leftAmount: 0, rightAmount: 8 }), null);
    assert.equal(stockPerRecipeUnit({ ...base, leftAmount: -1, rightAmount: 8 }), null);
    assert.equal(stockPerRecipeUnit({ ...base, leftAmount: 1, rightAmount: 0 }), null);
  });
});

describe("formatEchoAmount", () => {
  it("strips trailing zeros / renders whole numbers cleanly", () => {
    assert.equal(formatEchoAmount(125), "125");
    assert.equal(formatEchoAmount(124.5), "124.5");
    assert.equal(formatEchoAmount(0.125), "0.125");
  });

  it("renders nothing for null / non-positive / non-finite", () => {
    assert.equal(formatEchoAmount(null), "");
    assert.equal(formatEchoAmount(0), "");
    assert.equal(formatEchoAmount(-3), "");
    assert.equal(formatEchoAmount(Infinity), "");
  });
});
