// @ts-check
//
// VNode-level contract tests for the Preact island consumer of the canonical
// .stepper markup. No browser or DOM implementation is needed: callbacks are
// invoked directly on the VNodes the component emits.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import { QuantityStepper } from "../quantity-stepper.js";

/** @param {unknown} value @returns {any[]} */
function flattenChildren(value) {
  if (value == null || value === false) return [];
  return Array.isArray(value) ? value.flatMap(flattenChildren) : [value];
}

/** @param {any} vnode @returns {any[]} */
function childrenOf(vnode) {
  return flattenChildren(vnode?.props?.children);
}

describe("QuantityStepper VNode contract", () => {
  it("renders editable canonical markup and wires input/decrease/increase callbacks", () => {
    const calls = [];
    const vnode = QuantityStepper({
      value: "1.5",
      min: "0",
      step: "any",
      ariaLabel: "Quantity for flour",
      decreaseLabel: "Decrease flour",
      increaseLabel: "Increase flour",
      onInput: (raw) => calls.push(["input", raw]),
      onDecrease: () => calls.push(["decrease"]),
      onIncrease: () => calls.push(["increase"]),
    });

    assert.equal(vnode.type, "div");
    assert.equal(vnode.props.class, "stepper");
    assert.equal(vnode.props.role, "group");
    assert.equal(vnode.props["aria-label"], "Quantity for flour");

    const children = childrenOf(vnode);
    const buttons = children.filter((child) => child.type === "button");
    const input = children.find((child) => child.type === "input");
    assert.equal(buttons.length, 2);
    assert.equal(input.props.class, "stepper__val");
    assert.equal(input.props.type, "number");
    assert.equal(input.props.min, "0");
    assert.equal(input.props.step, "any");
    assert.equal(input.props.value, "1.5");
    assert.equal(buttons[0].props.class, "stepper__btn");
    assert.equal(buttons[1].props.class, "stepper__btn");
    assert.equal(buttons[0].props["aria-label"], "Decrease flour");
    assert.equal(buttons[1].props["aria-label"], "Increase flour");

    input.props.onInput({ target: { value: "2.25" } });
    buttons[0].props.onClick();
    buttons[1].props.onClick();
    assert.deepEqual(calls, [["input", "2.25"], ["decrease"], ["increase"]]);
  });

  it("renders compact display mode with disabled boundary state and no input", () => {
    const vnode = QuantityStepper({
      value: 1,
      variant: "compact",
      display: true,
      ariaLabel: "Servings for soup",
      decreaseDisabled: true,
      increaseDisabled: false,
      decreaseLabel: "Fewer servings",
      increaseLabel: "More servings",
    });

    assert.equal(vnode.props.class, "stepper stepper--compact");
    const children = childrenOf(vnode);
    const buttons = children.filter((child) => child.type === "button");
    const value = children.find((child) => child.type === "span");
    assert.equal(value.props.class, "stepper__val");
    assert.equal(value.props.children, "1");
    assert.equal(children.some((child) => child.type === "input"), false);
    assert.equal(buttons[0].props.disabled, true);
    assert.equal(buttons[1].props.disabled, false);
  });
});
