// @ts-check
//
// Unit tests for ingredient-amount.js (bead plantry-jun6).
//
// Run with: node --test  (from repo root)  or  npm test
// No npm dependencies — Node's built-in runner + assert, importing the ESM module directly.
//
// These pin the shared trailing-zero rule and its parity with the C# twin
// (src/Plantry.Web/Pages/Recipes/IngredientAmount.cs — see IngredientAmountTests.cs for the mirror).

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import { formatAmount, MAX_DECIMALS } from "../ingredient-amount.js";

describe("formatAmount — trailing-zero cleanup (acceptance cases)", () => {
  it("strips trailing zeros from a whole value: 500.000 → 500", () => {
    assert.equal(formatAmount(500.0), "500");
    assert.equal(formatAmount("500.000"), "500");
  });

  it("strips a single trailing zero: 1.50 → 1.5", () => {
    assert.equal(formatAmount(1.5), "1.5");
    assert.equal(formatAmount("1.50"), "1.5");
  });

  it("leaves a bare integer untouched: 1 → 1", () => {
    assert.equal(formatAmount(1), "1");
    assert.equal(formatAmount("1"), "1");
  });

  it("keeps a real fraction: 0.125 → 0.125", () => {
    assert.equal(formatAmount(0.125), "0.125");
  });

  it("renders zero as 0", () => {
    assert.equal(formatAmount(0), "0");
  });
});

describe("formatAmount — scaled/noisy tails are rounded", () => {
  it("rounds a repeating decimal to MAX_DECIMALS places (100/3)", () => {
    assert.equal(formatAmount(100 / 3), "33.3333");
  });

  it("rounds and strips: 200/3 → 66.6667", () => {
    assert.equal(formatAmount(200 / 3), "66.6667");
  });

  it("does not invent precision beyond the source: 2.5 → 2.5", () => {
    assert.equal(formatAmount(2.5), "2.5");
  });

  it("MAX_DECIMALS is 4 (kept in lock-step with the C# twin)", () => {
    assert.equal(MAX_DECIMALS, 4);
  });
});

describe("formatAmount — empty / invalid inputs render nothing", () => {
  for (const bad of [null, undefined, "", "abc", NaN, Infinity, -Infinity]) {
    it(`returns "" for ${String(bad)}`, () => {
      // @ts-expect-error — deliberately exercising non-numeric inputs
      assert.equal(formatAmount(bad), "");
    });
  }
});
