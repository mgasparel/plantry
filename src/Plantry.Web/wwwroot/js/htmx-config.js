// Disables htmx's id-based "attribute settling" for class/style/width/height (plantry-nuvm).
//
// htmx.config.attributesToSettle defaults to ["class","style","width","height"]. It exists to
// smooth CSS transitions: when an incoming swap contains an element whose id matches one already
// live in the DOM, htmx temporarily copies the OLD element's listed attribute values onto the NEW
// element right after the swap, then — after config.settleDelay (20ms) — restores the attribute
// values from the server-rendered markup itself, overwriting whatever is on the element at that
// moment.
//
// Plantry has no CSS transitions that rely on this. What it DOES have is Alpine components that
// reactively own class/style on elements with a stable, reused id (e.g. #plan-rail's `:class`
// binding, #plan-rail-reopen's `x-show`) inside fragments that get replaced wholesale via
// hx-swap="innerHTML" (every MealPlan grid handler re-renders #plan-main-content this way). The
// settle step runs ~20ms after the swap, AFTER Alpine's MutationObserver-driven init has already
// applied the correct reactive class/style — and then clobbers it back to the plain
// server-rendered value (which never carries an inline style, since Alpine owns that), silently
// undoing Alpine's binding. Caught by MealPlanRailBreakpointE2ETests.
// GridReRender_ThenRotation_RailResyncsWithNoLeakedListener: after a per-day auto-fill swap,
// #plan-rail-reopen stayed visible (`style` wiped back to server-rendered "none") even though
// Alpine's own reactive state correctly said the rail should stay open/hidden.
//
// Alpine already manages these attributes reactively wherever it's used, so htmx's assist is
// redundant at best and actively harmful here — disable it globally rather than special-case
// every id-stable, Alpine-bound element.
(function () {
    'use strict';
    htmx.config.attributesToSettle = [];
})();
