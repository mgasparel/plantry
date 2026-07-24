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

// ── formatAmount(value, "fraction") — Q1 vulgar-fraction snap (plantry-95w5) ────────────────────────
// Mirrors the C# QuantityDisplayTests.cs FormatAmount_Fraction_* cases (QuantityDisplay.FormatAmount).

describe("formatAmount — fraction style: bare glyphs (§3)", () => {
  const cases = [
    [0.5, "½"],
    [0.25, "¼"],
    [0.75, "¾"],
    [0.125, "⅛"],
    [0.375, "⅜"],
    [0.625, "⅝"],
    [0.875, "⅞"],
  ];
  for (const [amount, expected] of cases) {
    it(`${amount} → "${expected}"`, () => {
      assert.equal(formatAmount(amount, "fraction"), expected);
    });
  }

  it("thirds snap: 1/3 → ⅓, 2/3 → ⅔", () => {
    assert.equal(formatAmount(1 / 3, "fraction"), "⅓");
    assert.equal(formatAmount(2 / 3, "fraction"), "⅔");
  });
});

describe("formatAmount — fraction style: mixed numbers, no separator", () => {
  const cases = [
    [1.5, "1½"],
    [1.75, "1¾"],
    [2.25, "2¼"],
  ];
  for (const [amount, expected] of cases) {
    it(`${amount} → "${expected}"`, () => {
      assert.equal(formatAmount(amount, "fraction"), expected);
    });
  }
});

describe("formatAmount — fraction style: whole numbers and carry", () => {
  it("renders a whole number plainly: 4 → 4, 1 → 1", () => {
    assert.equal(formatAmount(4, "fraction"), "4");
    assert.equal(formatAmount(1, "fraction"), "1");
  });

  it("a remainder near whole carries up: 1.997 → 2, 2.004 → 2", () => {
    assert.equal(formatAmount(1.997, "fraction"), "2");
    assert.equal(formatAmount(2.004, "fraction"), "2");
  });
});

describe("formatAmount — fraction style: tolerance boundary around ⅓", () => {
  it("0.34 is within 0.01 of ⅓ → snaps; 0.32 is 0.0133 away → decimal fallback", () => {
    assert.equal(formatAmount(0.34, "fraction"), "⅓");
    assert.equal(formatAmount(0.32, "fraction"), "0.32");
  });
});

describe("formatAmount — fraction style: non-snapping falls back to decimal", () => {
  it("0.6 is not near any vocabulary fraction (⅝ = 0.625 is 0.025 away)", () => {
    assert.equal(formatAmount(0.6, "fraction"), "0.6");
    assert.equal(formatAmount(0.55, "fraction"), "0.55");
  });
});

describe("formatAmount — fraction style: the repro case (plantry-95w5)", () => {
  it("¼ cup scaled to 0.5× → 0.125 snaps to ⅛, not the bare decimal", () => {
    assert.equal(formatAmount(0.25 * 0.5, "fraction"), "⅛");
  });
});

describe("formatAmount — decimal style / no style is unaffected by the snap", () => {
  it("0.125 stays a bare decimal without the fraction style", () => {
    assert.equal(formatAmount(0.125, "decimal"), "0.125");
    assert.equal(formatAmount(0.125), "0.125");
  });

  it("a snappable value with no style passed renders as the historical decimal", () => {
    assert.equal(formatAmount(0.5), "0.5");
  });
});

describe("formatAmount — fraction style: non-positive amounts keep the decimal render", () => {
  it("zero and negative amounts are untouched by the snap", () => {
    assert.equal(formatAmount(0, "fraction"), "0");
    assert.equal(formatAmount(-0.5, "fraction"), "-0.5");
  });
});

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
