// @ts-check
//
// Shared toast controller + render snippet for Preact islands (bead plantry-55bv).
//
// Consolidates two independently hand-rolled, drifted toast implementations:
//   - take-stock.js: a bare `toast` string signal, click-to-dismiss only, no timer.
//   - intake-review.js: a `toastMsg`/`toastUndo` signal pair behind a `showToast`
//     helper with a 6000ms auto-dismiss timer and an optional Undo action.
// Both rendered the identical `.toast` markup (icon + message) by copy-paste, which
// is exactly the drift "extract before you repeat" (this repo's UI convention) exists
// to prevent. This module is the single implementation; both islands now consume it.
//
// A sibling to helpers.js, not folded into it — helpers.js is scoped to
// transport/hydration only (see its own docstring). This module owns toast UI state
// instead.
//
// ADR-020 §7 tripwire — free of runtime imports, like the *-logic.js modules:
// `signal` and `html` are injected by the caller (the island's mount function and
// its App component), which already import the island's one runtime.js instance.
// Importing runtime.js again here would risk instantiating a second, disconnected
// copy of the Preact/signals module graph — see the injected-factory precedent in
// take-stock.js's `makeRow` wrapper for the same reasoning.
//
// Usage in an island:
//   import { signal, html } from "./runtime.js?v=1";
//   import { createToast, createToastHost } from "./toast.js?v=1";
//
//   const toast = createToast(signal);
//   const ToastHost = createToastHost(html);
//
//   toast.show("Saved");
//   toast.show("Item removed", () => undoRemove());
//   // ...in the render tree:  html`<${ToastHost} toast=${toast} />`

/**
 * Auto-dismiss duration in ms. Adopted from Intake Review, the more complete of the
 * two prior implementations — this is an intentional behavior change for Take Stock,
 * whose toast previously persisted until clicked.
 */
export const TOAST_AUTO_DISMISS_MS = 6000;

/**
 * @typedef {Object} Toast
 * @property {import("@preact/signals").Signal<string>} msg
 * @property {import("@preact/signals").Signal<(() => void | Promise<void>) | null>} undo
 * @property {(msg: string, undoFn?: (() => void | Promise<void>) | null) => void} show
 * @property {() => void} hide
 */

/**
 * Create a toast controller: a message signal + optional undo-callback signal,
 * sharing one auto-dismiss timer that resets on every `show()`.
 *
 * @param {<T>(v: T) => import("@preact/signals").Signal<T>} signalFn  injected `signal` factory
 * @returns {Toast}
 */
export function createToast(signalFn) {
  const msg = signalFn("");
  const undo = signalFn(/** @type {(() => void | Promise<void>) | null} */ (null));
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let timer;

  function hide() {
    clearTimeout(timer);
    timer = undefined;
    msg.value = "";
    undo.value = null;
  }

  /**
   * @param {string} text
   * @param {(() => void | Promise<void>) | null} [undoFn]
   */
  function show(text, undoFn) {
    clearTimeout(timer);
    msg.value = text;
    undo.value = undoFn ?? null;
    timer = setTimeout(hide, TOAST_AUTO_DISMISS_MS);
  }

  return { msg, undo, show, hide };
}

/**
 * Build the shared toast host component, bound to the caller's own `html` tag
 * function (injected — see module header on why this file never imports runtime.js).
 * Renders the icon + message, plus an Undo action when the current toast carries an
 * undo callback. Clicking anywhere on the toast except the Undo button dismisses it.
 *
 * @param {Function} html  the island's htm-bound tag function
 * @returns {(props: { toast: Toast }) => unknown}
 */
export function createToastHost(html) {
  return function ToastHost({ toast }) {
    if (!toast.msg.value) return null;
    const undoFn = toast.undo.value;
    return html`
      <div class="toast" role="status" aria-live="polite"
           onClick=${(/** @type {MouseEvent} */ e) => {
             if (!(/** @type {HTMLElement} */ (e.target).closest("[data-action]"))) toast.hide();
           }}>
        <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg>
        <span>${toast.msg.value}</span>
        ${undoFn && html`<button type="button" class="toast__action" data-action="undo"
               onClick=${() => { toast.hide(); undoFn(); }}>Undo</button>`}
      </div>`;
  };
}
