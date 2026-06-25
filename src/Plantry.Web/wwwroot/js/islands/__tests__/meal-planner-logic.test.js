// @ts-check
//
// Unit tests for meal-planner-logic.js (ADR-020, bead plantry-2zvm.12).
//
// Run with: node --test  (from repo root)
// Or:       npm test
//
// No npm dependencies — uses Node's built-in test runner and assert module.
// Imports the island logic module directly as ESM; no browser globals needed
// (the logic functions are pure transforms of their arguments).
//
// applyMutationResult is NOT tested here: it manipulates the live DOM and is
// covered by the Playwright E2E suite instead.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import { lvl, money, dishMeta } from "../meal-planner-logic.js";

// ── test helpers ─────────────────────────────────────────────────────────────

/**
 * Build a minimal DishDraft with sensible defaults.
 *
 * @param {Partial<import("../meal-planner-logic.js").DishDraft>} overrides
 * @returns {import("../meal-planner-logic.js").DishDraft}
 */
function dish(overrides = {}) {
  return {
    kind: "recipe",
    itemId: "recipe-1",
    name: "Spaghetti Bolognese",
    servings: 2,
    fulfillment: 80,
    costPerServing: 3.50,
    hasPhoto: false,
    ...overrides,
  };
}

// ── lvl ───────────────────────────────────────────────────────────────────────

describe("lvl", () => {
  it("returns 'hi' at exactly 80 (lower boundary)", () => {
    assert.equal(lvl(80), "hi");
  });

  it("returns 'mid' at 79 (one below hi threshold)", () => {
    assert.equal(lvl(79), "mid");
  });

  it("returns 'mid' at exactly 50 (lower boundary)", () => {
    assert.equal(lvl(50), "mid");
  });

  it("returns 'lo' at 49 (one below mid threshold)", () => {
    assert.equal(lvl(49), "lo");
  });

  it("returns 'lo' for null", () => {
    assert.equal(lvl(null), "lo");
  });

  it("returns 'hi' above 80", () => {
    assert.equal(lvl(100), "hi");
    assert.equal(lvl(95), "hi");
  });

  it("returns 'mid' between 50 and 79 inclusive", () => {
    assert.equal(lvl(65), "mid");
    assert.equal(lvl(51), "mid");
  });

  it("returns 'lo' below 50", () => {
    assert.equal(lvl(0), "lo");
    assert.equal(lvl(1), "lo");
    assert.equal(lvl(49), "lo");
  });
});

// ── money ─────────────────────────────────────────────────────────────────────

describe("money", () => {
  it("formats zero as '$0.00'", () => {
    assert.equal(money(0), "$0.00");
  });

  it("formats a whole number with two decimal places", () => {
    assert.equal(money(5), "$5.00");
  });

  it("formats a one-decimal value with trailing zero", () => {
    assert.equal(money(3.5), "$3.50");
  });

  it("formats an already-two-decimal value unchanged", () => {
    assert.equal(money(3.99), "$3.99");
  });

  it("rounds a three-decimal value to two places", () => {
    // 3.505 rounds to 3.51 under standard JS toFixed rounding
    // (actual JS rounding can be implementation-specific for exact half-values;
    // test a clearly-above-halfway case to be deterministic)
    assert.equal(money(3.506), "$3.51");
  });

  it("formats a large value correctly", () => {
    assert.equal(money(1234.56), "$1234.56");
  });

  it("prefixes with '$' always", () => {
    assert.ok(money(0).startsWith("$"));
    assert.ok(money(99.99).startsWith("$"));
  });
});

// ── dishMeta ──────────────────────────────────────────────────────────────────

describe("dishMeta", () => {
  it("returns 'pantry item' when fulfillment is null", () => {
    const d = dish({ fulfillment: null, costPerServing: 2.50, servings: 1 });
    assert.equal(dishMeta(d), "pantry item");
  });

  it("returns fulfillment string without cost when costPerServing is null", () => {
    const d = dish({ fulfillment: 80, costPerServing: null, servings: 2 });
    assert.equal(dishMeta(d), "80% in pantry");
  });

  it("includes cost portion when both fulfillment and costPerServing are set", () => {
    // servings:2, costPerServing:3.50 → total $7.00
    const d = dish({ fulfillment: 75, costPerServing: 3.50, servings: 2 });
    assert.equal(dishMeta(d), "75% in pantry · $7.00");
  });

  it("uses servings:1 when servings is falsy (0)", () => {
    // d.servings || 1 means servings=0 falls back to 1
    const d = dish({ fulfillment: 60, costPerServing: 4.00, servings: 0 });
    assert.equal(dishMeta(d), "60% in pantry · $4.00");
  });

  it("uses actual servings count in cost calculation", () => {
    const d = dish({ fulfillment: 90, costPerServing: 2.00, servings: 3 });
    assert.equal(dishMeta(d), "90% in pantry · $6.00");
  });

  it("shows 0% fulfillment as '0% in pantry'", () => {
    const d = dish({ fulfillment: 0, costPerServing: null });
    assert.equal(dishMeta(d), "0% in pantry");
  });

  it("shows 100% fulfillment correctly", () => {
    const d = dish({ fulfillment: 100, costPerServing: 5.00, servings: 1 });
    assert.equal(dishMeta(d), "100% in pantry · $5.00");
  });

  it("cost arithmetic: costPerServing * servings is formatted with money()", () => {
    // Verify the number formatting matches money() exactly
    const d = dish({ fulfillment: 50, costPerServing: 1.99, servings: 3 });
    // 1.99 * 3 = 5.97
    assert.equal(dishMeta(d), "50% in pantry · $5.97");
  });
});
