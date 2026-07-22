// @ts-check
//
// Unit tests for ingredient-anchor.js (bead plantry-c7mg).
//
// Run with: node --test  (from repo root)  or  npm test
// No npm dependencies — Node's built-in runner + assert, importing the ESM module directly.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import { parseIngredientAnchor } from "../ingredient-anchor.js";

describe("parseIngredientAnchor — valid anchors", () => {
  it("parses a simple ordinal with the leading #", () => {
    assert.equal(parseIngredientAnchor("#ingredient-3"), 3);
  });

  it("parses without a leading # (location.hash sometimes omits it in tests)", () => {
    assert.equal(parseIngredientAnchor("ingredient-3"), 3);
  });

  it("parses ordinal 0", () => {
    assert.equal(parseIngredientAnchor("#ingredient-0"), 0);
  });

  it("parses a multi-digit ordinal", () => {
    assert.equal(parseIngredientAnchor("#ingredient-42"), 42);
  });
});

describe("parseIngredientAnchor — non-matches return null", () => {
  for (const bad of [null, undefined, "", "#", "#ingredient-", "#ingredient-abc", "#ingredient--1", "#other-3", "#ingredient-3-extra", "#ingredient-3.5"]) {
    it(`returns null for ${JSON.stringify(bad)}`, () => {
      assert.equal(parseIngredientAnchor(bad), null);
    });
  }
});
