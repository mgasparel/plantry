// @ts-check
//
// Island consumer for the component-library stepper (ADR-020).  The Razor
// <stepper> tag helper and this component deliberately emit the same
// .stepper/.stepper__btn/.stepper__val contract.  Keeping the island consumer
// here prevents feature pages from copying the primitive's button/value
// markup while still allowing a Preact island to own its draft callbacks.

import { html } from "./runtime.js?v=1";

/**
 * @typedef {Object} QuantityStepperProps
 * @property {number|string|null} value
 * @property {number|string} [min]
 * @property {number|string} [step]
 * @property {string} [ariaLabel]
 * @property {string} [decreaseLabel]
 * @property {string} [increaseLabel]
 * @property {string} [variant]
 * @property {string} [inputName]
 * @property {boolean} [disabled]
 * @property {boolean} [decreaseDisabled]
 * @property {boolean} [increaseDisabled]
 * @property {(raw:string) => void} [onInput]
 * @property {() => void} [onDecrease]
 * @property {() => void} [onIncrease]
 */

/**
 * Render the canonical −/[value]/+ stepper contract for a Preact island.
 *
 * Input mode is used for editable quantities.  Passing `display=true` is not
 * necessary: a display value is represented by omitting `onInput`, which keeps
 * the component useful for recipe servings without creating a second markup
 * shape.  Both modes retain the same role, button labels, icon affordances and
 * `.stepper__val` slot used by the Razor tag helper.
 *
 * @param {QuantityStepperProps & { display?: boolean }} props
 */
export function QuantityStepper({
  value,
  min = 0,
  step = "any",
  ariaLabel = "Quantity",
  decreaseLabel = "Decrease quantity",
  increaseLabel = "Increase quantity",
  variant = "",
  inputName = "",
  disabled = false,
  decreaseDisabled = false,
  increaseDisabled = false,
  onInput,
  onDecrease,
  onIncrease,
  display = false,
}) {
  const classes = ["stepper", variant ? `stepper--${variant}` : ""].filter(Boolean).join(" ");
  const valueText = value == null ? "" : String(value);

  return html`
    <div class=${classes} role="group" aria-label=${ariaLabel}>
      <button type="button" class="stepper__btn"
              aria-label=${decreaseLabel}
              disabled=${disabled || decreaseDisabled}
              onClick=${onDecrease}>
        <svg class="icon" aria-hidden="true"><use href="#i-minus" /></svg>
      </button>
      ${display
        ? html`<span class="stepper__val">${valueText}</span>`
        : html`<input class="stepper__val" type="number"
                       name=${inputName}
                       min=${min}
                       step=${step}
                       value=${valueText}
                       disabled=${disabled}
                       aria-label=${ariaLabel}
                       onInput=${(/** @type {InputEvent} */ e) =>
                         onInput?.(/** @type {HTMLInputElement} */ (e.target).value)} />`}
      <button type="button" class="stepper__btn"
              aria-label=${increaseLabel}
              disabled=${disabled || increaseDisabled}
              onClick=${onIncrease}>
        <svg class="icon" aria-hidden="true"><use href="#i-plus" /></svg>
      </button>
    </div>
  `;
}
