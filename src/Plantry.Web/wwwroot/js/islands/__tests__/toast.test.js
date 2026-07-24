// @ts-check
//
// Unit tests for toast.js (bead plantry-55bv).
//
// Run with: node --test  (from repo root)
// Or:       npm test
//
// No npm dependencies — uses Node's built-in test runner and assert module, plus its
// built-in fake timers (node:test mock.timers) to deterministically exercise the
// auto-dismiss behaviour without real waits.
//
// createToastHost (the render snippet) is not exercised here — it is a thin Preact
// component wrapper with no logic of its own beyond what createToast already covers;
// its markup is exercised indirectly via the take-stock/intake-review E2E suites.

import { describe, it, mock } from "node:test";
import assert from "node:assert/strict";

import { createToast, TOAST_AUTO_DISMISS_MS } from "../toast.js";

// ── test helpers ─────────────────────────────────────────────────────────────

/**
 * Minimal signal stub — a plain object with a writable `value` property, matching
 * the pattern used by take-stock-logic.test.js / intake-review-logic.test.js. The
 * toast controller only reads/writes `.value`, never signal-specific methods.
 *
 * @template T
 * @param {T} v
 * @returns {{ value: T }}
 */
function sig(v) {
  return { value: v };
}

describe("createToast", () => {
  it("show() sets the message and clears any undo callback by default", () => {
    const toast = createToast(sig);
    toast.show("Saved");
    assert.equal(toast.msg.value, "Saved");
    assert.equal(toast.undo.value, null);
  });

  it("show() threads an undo callback through the undo signal", () => {
    const toast = createToast(sig);
    const undoFn = () => {};
    toast.show("Item removed", undoFn);
    assert.equal(toast.msg.value, "Item removed");
    assert.equal(toast.undo.value, undoFn);
  });

  it("hide() clears the message and undo callback immediately", () => {
    const toast = createToast(sig);
    toast.show("Saved", () => {});
    toast.hide();
    assert.equal(toast.msg.value, "");
    assert.equal(toast.undo.value, null);
  });

  it("auto-dismisses after the configured duration", (t) => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    const toast = createToast(sig);
    toast.show("Saved");
    assert.equal(toast.msg.value, "Saved");

    t.mock.timers.tick(TOAST_AUTO_DISMISS_MS - 1);
    assert.equal(toast.msg.value, "Saved", "must not dismiss before the duration elapses");

    t.mock.timers.tick(1);
    assert.equal(toast.msg.value, "", "must auto-dismiss once the duration elapses");
  });

  it("a second show() before the timer fires resets the timer — no premature dismissal", (t) => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    const toast = createToast(sig);
    toast.show("First");

    t.mock.timers.tick(TOAST_AUTO_DISMISS_MS - 1);
    toast.show("Second");
    assert.equal(toast.msg.value, "Second");

    // The original timer's remaining 1ms elapses — must NOT dismiss "Second" prematurely.
    t.mock.timers.tick(1);
    assert.equal(toast.msg.value, "Second", "the reset timer must not fire early");

    // Only after a full fresh duration from the second show() does it dismiss.
    t.mock.timers.tick(TOAST_AUTO_DISMISS_MS - 1);
    assert.equal(toast.msg.value, "", "the reset timer must fire once its own full duration elapses");
  });

  it("hide() cancels a pending auto-dismiss timer (no stray callback after an explicit hide)", (t) => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    const toast = createToast(sig);
    toast.show("Saved");
    toast.hide();
    toast.show("Next"); // if the old timer weren't cancelled, it could clobber this later

    t.mock.timers.tick(TOAST_AUTO_DISMISS_MS);
    assert.equal(toast.msg.value, "", "the second toast's own timer dismisses it on schedule");
  });

  it("undo callback fires and the toast state reflects a manual hide + fn call (host click behaviour)", async () => {
    const toast = createToast(sig);
    let fired = false;
    toast.show("Item removed", () => { fired = true; });

    // Mirrors createToastHost's Undo button handler: hide() then invoke the callback.
    const fn = toast.undo.value;
    toast.hide();
    assert.equal(toast.msg.value, "");
    assert.equal(toast.undo.value, null);
    if (fn) await fn();
    assert.equal(fired, true);
  });
});
