// @ts-check
//
// Contract tests for the island counterpart of Shared/_UnitOptionGroups.cshtml.
// The server decides reachability; this pure transform proves that the island
// still presents the server-supplied canonical dimension/code order and leaves
// stale values out of the selectable list.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import { UnitPicker, groupUnitOptions } from "../unit-picker.js";

/** @param {unknown} value @returns {any[]} */
function flattenChildren(value) {
  if (value == null || value === false) return [];
  return Array.isArray(value) ? value.flatMap(flattenChildren) : [value];
}

/** @param {any} vnode */
function selectFrom(vnode) {
  return flattenChildren(vnode?.props?.children).find((child) => child?.type === "select");
}

describe("groupUnitOptions", () => {
  it("preserves the server sequence without browser-locale re-sorting", () => {
    const groups = groupUnitOptions([
      // Deliberately non-alphabetical within each dimension: the adapter's
      // sequence is authoritative and this helper must not re-sort it.
      { unitId: "mass-z", code: "z", dimension: "Mass" },
      { unitId: "mass-a", code: "a", dimension: "Mass" },
      { unitId: "volume-z", code: "z", dimension: "Volume" },
      { unitId: "volume-a", code: "a", dimension: "Volume" },
      { unitId: "srv", code: "srv", dimension: "Count" },
    ]);

    assert.deepEqual(groups.map((g) => g.dimension), ["Mass", "Volume", "Count"]);
    assert.deepEqual(groups[0].options.map((o) => o.code), ["z", "a"]);
    assert.deepEqual(groups[1].options.map((o) => o.code), ["z", "a"]);
    assert.deepEqual(groups[2].options.map((o) => o.code), ["srv"]);
  });

  it("preserves an unknown dimension as supplied without relabelling it", () => {
    const groups = groupUnitOptions([
      { unitId: "g", code: "g", dimension: "Mass" },
      { unitId: "x", code: "x", dimension: "Unknown" },
    ]);

    assert.deepEqual(groups.map((g) => g.dimension), ["Mass", "Unknown"]);
  });
});

describe("UnitPicker VNode contract", () => {
  it("renders canonical optgroups and retains a selected non-first default", () => {
    let changed = "";
    const vnode = UnitPicker({
      options: [
        { unitId: "g", code: "g", dimension: "mass" },
        { unitId: "kg", code: "kg", dimension: "mass" },
        { unitId: "ml", code: "mL", dimension: "volume" },
        { unitId: "srv", code: "srv", dimension: "count" },
      ],
      selectedUnitId: "srv",
      staleUnitCode: null,
      onChange: (unitId) => { changed = unitId; },
    });
    const select = selectFrom(vnode);
    assert.ok(select);
    assert.equal(select.props.value, "srv");

    const groups = flattenChildren(select.props.children).filter((child) => child?.type === "optgroup");
    assert.deepEqual(groups.map((group) => group.props.label), ["Mass", "Volume", "Count"]);
    assert.deepEqual(groups.map((group) => flattenChildren(group.props.children)
      .map((option) => option.props.children)), [["g", "kg"], ["mL"], ["srv"]]);
    assert.equal(flattenChildren(select.props.children).some(
      (child) => child?.type === "option" && child.props.value === "",
    ), false);

    select.props.onChange({ target: { value: "g" } });
    assert.equal(changed, "g");
  });

  it("keeps unknown dimensions selectable without creating an unknown optgroup", () => {
    const vnode = UnitPicker({
      options: [
        { unitId: "g", code: "g", dimension: "Mass" },
        { unitId: "x", code: "x", dimension: "Unknown" },
        { unitId: "srv", code: "srv", dimension: "Count" },
      ],
      selectedUnitId: "x",
      staleUnitCode: null,
      onChange: () => {},
    });
    const select = selectFrom(vnode);
    const children = flattenChildren(select.props.children);
    const groups = children.filter((child) => child?.type === "optgroup");

    assert.equal(select.props.value, "x");
    assert.deepEqual(groups.map((group) => group.props.label), ["Mass", "Count"]);
    assert.equal(groups.some((group) => group.props.label === "Unknown"), false);
    assert.deepEqual(
      children.filter((child) => child?.type === "option").map((option) => option.props.value),
      ["x"],
    );
  });

  it("keeps the placeholder and stale value visible without making stale selectable", () => {
    const vnode = UnitPicker({
      options: [{ unitId: "g", code: "g", dimension: "Mass" }],
      selectedUnitId: "",
      staleUnitCode: "srv",
      onChange: () => {},
    });
    const stale = flattenChildren(vnode.props.children).find((child) => child?.type === "span");
    assert.equal(stale?.props.class, "meal-unit-picker__stale");
    assert.equal(stale?.props.children, "srv");

    const select = selectFrom(vnode);
    const options = flattenChildren(select.props.children)
      .flatMap((child) => child?.type === "optgroup" ? flattenChildren(child.props.children) : [child])
      .filter((child) => child?.type === "option");
    const placeholder = options.find((option) => option.props.value === "");
    assert.equal(placeholder?.props.disabled, true);
    assert.equal(options.some((option) => option.props.value === "srv"), false);
    assert.equal(options.some((option) => option.props.children === "srv"), false);
  });
});
