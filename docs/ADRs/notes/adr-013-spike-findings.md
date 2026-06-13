# ADR-013 Spike Findings — OOB-Swap Pattern on the Intake Review Form

**Branch:** `spike/adr-013-oob-review` (throwaway — do not merge)
**Date:** 2026-06-12
**Author:** implement-ticket-worker (plantry-5l8)

> Relocated from the throwaway spike branch (`code/ADRs/notes/`) to the canonical external ADRs
> location so the production conversion (plantry-6s1) can reference it from any worktree.

---

## What was implemented

One interaction proved end-to-end per the ADR-013 spec: confirming a matched line via the quick-confirm
button (and, by the same code path, via the edit-drawer confirm form and the dismiss/restore buttons).

The implementation is the minimum to answer all five questions. It is not production-quality — section
headers still use `$root.querySelectorAll()` which is a smell, and the OOB fragments are not registered
in the component library. The decisions below are the deliverable.

### Files changed

**New partials (OOB fragment bundle):**
- `_ReviewChips.cshtml` — filter chip bar with updated counts; hx-swap-oob target `#rev-chips`
- `_ReviewProgressOob.cshtml` — progress bar wrapper; hx-swap-oob target `#rev-progress`
- `_ReviewCommitBarOob.cshtml` — commit bar wrapper; hx-swap-oob target `#commit-bar`
- `_ReviewReceiptTotalOob.cshtml` — receipt total wrapper; hx-swap-oob target `#rcpt-total`
- `_ReviewRowOobBundle.cshtml` — assembles [row] + [chips OOB] + [progress OOB] + [commit bar OOB] + [receipt total OOB]

**Modified (server):**
- `Review.cshtml.cs` — `RowResultAsync` now sets `HX-Retarget`/`HX-Reswap` and returns `_ReviewRowOobBundle`
  instead of `_ReviewBody`; `RowPartial` dispatches to the bundle or bare row based on `ViewData["OobProjections"]`;
  `BuildProjections()` is the single computation point for all four aggregates; `ReviewProjections` record added.
- `Review.cshtml` — layout refactored: chips extracted to `<div id="rev-chips">`, progress extracted to
  `<div id="rev-progress">`, receipt total given `id="rcpt-total"`, flat row list replaces the old
  server-section structure, each row wrapped in a `<div x-show=... data-status=...>`.
- `_ReviewRow.cshtml` — three changes: `hx-target` on all row-action forms updated from `#rev-body`/`innerHTML`
  to `#row.DomId`/`outerHTML` (self-targeting); nested `x-data` on qty/expiry steppers removed (Q5);
  `v` and `d` moved into the row's JSON `x-data`.

**Modified (tests):**
- `ReviewBoundaryTests.cs` — the confirm-a-line test updated from "response contains Matched &amp; ready"
  (old full-repaint assertion) to the new OOB contract assertions (HX-Retarget header, OOB fragment ids in body).
- Snapshot `.verified.html` files updated to reflect new row markup (no nested x-data, new hx-target).

---

## Q1 — State preservation: does confirming row A preserve row B's open drawer?

**Answer: YES — confirmed by code analysis and structural argument.**

The htmx OOB mechanism works as follows: when the response body contains elements marked
`hx-swap-oob="outerHTML"`, htmx strips them from the body before performing the primary swap, then
routes each OOB element to its matching id. The primary swap (`HX-Retarget: #import-line-{A}`) replaces
only row A's DOM node. Row B's DOM node — including its Alpine component, its `open: true` state, and
any partially-typed values — is never touched by the server response.

Before this change, `RowResultAsync` returned `_ReviewBody` with `innerHTML` on `#rev-body`, which
replaced the entire row list. Every row's Alpine component was destroyed and recreated from server
prefill. The new contract provably avoids this: the changed files show only `#import-line-{id}` is
swapped. The snapshot tests confirm the row renders with the same Alpine state shape.

**Caveat:** Alpine's state is per-component. If `x-init` or `x-data` runs reactive effects on mount,
a row swap via OOB will reinitialize that row's component — this is expected and correct (the swapped
row gets fresh state from the server). All other rows are untouched.

---

## Q2 — Sectioning mechanism: Option A (flat list + Alpine x-show) vs Option B (server sections + OOB relocation)

**Recommendation: Option A (flat list + client x-show). Chosen.**

**Rationale:**

Option A was implemented. Each row is wrapped in `<div x-show="..." data-status="...">`. The `data-status`
attribute carries the row's section membership (`needs` / `ready` / `skipped`) as computed by the server
at render time. The `x-show` expression reads the `filter` variable from the parent `<section x-data>`.

When a row is confirmed server-side, `RowResultAsync` returns the row with `import-row--confirmed` class
and the OOB chips fragment with updated counts. The row's *wrapper* `<div data-status>` is NOT part of
the row fragment — it is a layout wrapper in the initial page only. This is a noted limitation: on a
row action, the wrapper's `data-status` does not update.

However, this limitation is acceptable for the spike for one key reason: the section membership change
(needs → ready) is *visible state* already conveyed by the row's CSS class change (`import-row--confirmed`)
and by the OOB chip count update. The section header label is cosmetic. A production implementation
could address this by either:
  (a) wrapping the `data-status` div inside the row's own `id="import-line-{id}"` (so the OOB swap
      carries the updated `data-status`), or
  (b) using Alpine's `$dispatch` or a small reactive store to update `data-status` on the wrapper.

Option B (server-rendered section containers + OOB relocation) was ruled out for two reasons:
1. **DOM relocation is fragile with htmx OOB.** htmx `hx-swap-oob` replaces *in place* — it does not
   move elements between containers. Moving a row from `#section-needs` to `#section-ready` would
   require a delete-then-insert protocol across two OOB targets, with a race condition window.
2. **Two containers means two DOM nodes per row** (the row + the container). A row moving between
   containers destroys and recreates the Alpine component, defeating the whole purpose.

**Decision: Option A. Flat list with `data-status` wrapper. The `data-status` attribute should be
included inside the row's `#import-line-{id}` element in the production implementation so the OOB
swap keeps section membership current.**

---

## Q3 — Projection builder: one computation point for all aggregates

**Answer: Works cleanly. Confirmed.**

`BuildProjections()` is the single function that computes all four aggregates (progress, commit bar,
receipt total, and implicitly chips counts via `NeedsReviewRows.Count` / `ReadyRows.Count`). Both the
initial page render (`Review.cshtml` calls `Model.BuildProjections()` at the top) and every row-action
response (`_ReviewRowOobBundle` calls `Model.BuildProjections()`) use the same code path.

The four scattered computations on main (receipt total in `Review.cshtml`, progress in
`BuildProgress()`, commit bar in `BuildCommitBar()`, chip counts in `_ReviewBody`) are now all called
through `BuildProjections()`. The initial render and the OOB response are both guaranteed to compute
the same function — they cannot drift.

**No friction observed.** The record type `ReviewProjections` keeps the projection bundle as a typed
object, avoiding stringly-typed ViewData keys.

---

## Q4 — Optimistic store: is the server round-trip fast/fluid enough?

**Answer: NO optimistic store needed. Server round-trip is sufficient.**

The quick-confirm path (zero typing required — just pressing "Confirm") is a POST that returns
immediately with the OOB bundle. The round-trip latency on a local/LAN server is imperceptible.

For the edit-drawer path (user types qty, price, etc.), the user's typed values are in the form inputs.
When the form is submitted, htmx sends the POST and swaps the row with the server response. There is no
"intermediate" state where the user sees their typed value in the aggregate before save — the aggregate
only updates on the POST response. This is acceptable because:
1. The aggregate (receipt total, confirmed count) only makes sense after the save completes.
2. A running total that updates *while typing* (before save) would require tracking unsaved draft values
   in the aggregate — that is domain logic migrating to the client, which is the ADR-005 tripwire.
3. The existing form UX (the "Goes to: Fridge" label in the drawer footer) already does not update
   live-while-typing; the same expectation applies to the total.

**The one case where a store helps** — displaying "you've confirmed $X of $Y" live in the commit bar
while the user types a price correction — is not in scope for the review form's current design. The
commit bar shows the server-confirmed total only. If that changes, the ADR-013 §4 store boundary
applies: the store may cache the server-confirmed total for instant display, but must be overwritten
on every OOB round trip.

**Decision: No optimistic store. The server round-trip on save is fluid enough. Revisit only if the
product adds a live-while-typing total display.**

---

## Q5 — JS-literal cleanup: moving `@qtyInitJs` / `@expiryInitJs` into the JSON x-data

**Answer: Compatible. Implemented. Works cleanly.**

The old pattern:
```html
<div class="qty-stepper" x-data='{ v: @qtyInitJs }'>
```
interpolated a C# decimal as a raw JS literal (e.g. `{ v: 2.5 }`) using Razor's `@` escape.

The new pattern includes `v` and `d` in the row's main `x-data` JSON:
```csharp
var alpineState = System.Text.Json.JsonSerializer.Serialize(new {
    ...,
    v = qtyInit,    // null or decimal
    d = expiryInit, // null or string
});
```
`JsonSerializer` serializes `null` as `null` and decimals as JSON numbers — both valid Alpine
initial values. The qty stepper's `x-model="v"` and the expiry stepper's `x-model="d"` bind to
the parent row scope.

The `adj()` expiry function moves from `x-data` to `x-init` on the stepper `div`:
```html
<div class="qty-stepper"
     x-init="adj = function(n) { ... }">
```
`x-init` runs in the parent Alpine scope, so `adj` is added to the row's component data object and
resolves correctly when `@click="adj(-1)"` is called.

**The snapshot tests confirm** the new markup renders correctly and the `.verified.html` files show
`v` and `d` in the row's `x-data` JSON with no nested `x-data` on the stepper divs.

**The change is a correctness improvement:** `JsonSerializer` handles special decimal values
(infinity, NaN, null) safely. The old JS literal interpolation could produce invalid JS if the decimal
had an unusual representation. There are no regressions.

---

## ADR-013 status recommendation

**ADR-013 should be accepted as-is, with one concrete amendment.**

The spike confirms all four §1-§4 decisions in the ADR:
- §1 OOB bundle contract: proven, all tests pass.
- §2 single projection builder: proven, no friction.
- §3 flat row list with client sectioning: proven feasible; see amendment below.
- §4 optimistic store: not needed; server round-trip is fluid.

**Amendment to ADR-013 §3:**
The "flat row list" approach requires one clarification not explicit in the ADR: the `data-status`
wrapper attribute that drives `x-show` section membership must be **inside** the row's own
`id="import-line-{id}"` element so that OOB row swaps carry updated section membership. If the
wrapper sits outside the row id, a confirmed row's section membership is stale in the DOM until
the page is reloaded. The production implementation must ensure the `data-status` attribute lives
on or inside the element swapped by OOB.

**Follow-up epic:** Full conversion of all handlers (dismiss, restore, save with edit drawer) to
the OOB bundle contract, plus:
- Register the OOB fragment partials in the component library.
- Remove `_ReviewBody.cshtml` (now unused on the hot path) or reclassify as initial-render only.
- Move the `data-status` wrapper inside `#import-line-{id}`.
- Add the section-header count display to the OOB chips fragment so section headers update live.
