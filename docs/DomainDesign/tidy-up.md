# Tidy Up — Household Data-Health Scan with Dismissable Findings

**Status:** Accepted design · **Author:** design conversation (mgasparel + Claude) · **Date:** 2026-07-21
**Origin:** the `plantry-2hfi` investigation — 3 lb of Onion Yellow showed as "out" on Shopping because
its lots can't convert to the product's display unit. That bug gets fixed, but the *data gap* behind it
(a missing conversion) remains, and it is one instance of a class: Plantry's "just enough at entry time"
ethos deliberately lets users skip detail, which leaves gaps that silently degrade behaviour elsewhere.
Point solutions exist per flow (recipe editor blocks on missing conversions R7/C10, ADR-022 defers gaps
to AI seeding) but nothing looks across the household's data after the fact.
**Prototype:** `C:\Users\mgasp\OneDrive\Documents\Claude\Projects\Plantry\code\.preview\data-health-page.html`
(static, real plenish tokens; UX locked by eye 2026-07-21 — "Tidy Up" name + accent badge chosen over
"Data Health"/"Fix-ups", grouped-by-problem-type layout chosen over flat list, dismissed disclosure and
"All tidy" empty state approved, broom icon revised once).
**Scope:** ① a dedicated Tidy Up page listing detector findings grouped by problem class, ② a Manage-band
nav item carrying an open-finding count badge, ③ per-finding dismissal with reopen-on-fact-change,
④ the detector framework, with the two conversion-gap detectors (D1, D2) shipping in v1.
**Out of scope:** inline fixes on the finding card (v1 deep-links to the owning screen), AI-suggested
fixes (natural follow-up — ADR-022's seeding could prefill a factor), proactive notifications, the
`plantry-2hfi` adapter bug itself (a defect, fixed independently; Tidy Up surfaces the data gap that
triggered it, not the misclassification).

---

## 1. Motivation

Getting out of the user's way at entry time is the product's core bet — but every skipped detail is a
small debt: a lot logged in lb against an "ea" product, a recipe line in cups with no cup→g path, an
expired yogurt still counted, a staple with no low-stock threshold. Each gap is invisible where it was
created and only shows up as *misbehaviour somewhere else* (a false "out", a Cook that can't deduct, a
cost that's silently incomplete). The user can't fix what they can't see, and we never want the answer
to be "go audit your catalog." Tidy Up makes the debt visible in one place, explains the consequence in
plain language, and walks the user to the one screen where each gap is fixed — while staying calm:
it's upkeep, not an alarm, and any finding can be dismissed for good.

## 2. Decision summary

| # | Decision | Rationale |
|---|----------|-----------|
| T1 | **Name "Tidy Up"**, nav item in the **Manage band after Catalog**, broom icon, **accent-toned count badge** rendered only when open findings > 0 | Locked by eye. Verb-phrase name matches "Take Stock"; Manage band because it's household upkeep, not daily cooking. Accent (not warning) tone: "there's something to do," not "something is on fire" — nagging is the failure mode |
| T2 | **Page at `/TidyUp`**, findings **grouped one card per detector**: card head = problem-class title + one-sentence consequence; rows = subject, one-line specifics, faint per-finding consequence, deep-link verb, quiet Dismiss. Groups ordered by severity (behaviour-affecting first); empty groups don't render; `empty-state` "All tidy" when nothing is open | Locked by eye over the flat-list alternative. The card head teaches the gap's meaning once; rows stay terse. The empty state is the goal state and should feel like a small reward |
| T3 | **Deep-link fixes only in v1.** Every detector declares a fix destination (page + anchor/filter where available); the button names it: "Fix in Catalog →", "Review in Take Stock →", "Set alert in Pantry →" | Keeps v1 detector-cheap: no per-detector mini-editors. The owning screen already knows how to make the edit safely. Inline/AI-assisted fixes layer on later without rework |
| T4 | **Findings are computed live at page render — no persisted findings table.** New module **`src/Plantry.Housekeeping`** owns the `Finding` read model, the `IProblemDetector` contract, the `Dismissal` aggregate + repository port, and the query service that runs detectors, filters dismissed, groups and orders. **Detector implementations live in `src/Plantry.Composition/Housekeeping/`**, reading other contexts only through their existing application services / read ports | Live computation avoids a sync problem (a persisted finding going stale the moment the user fixes the gap elsewhere). Detectors are inherently cross-context read compositions — exactly the seam Composition exists for (same pattern as `ShoppingPantryReaderAdapter`). Housekeeping itself stays → SharedKernel only |
| T5 | **Dismissal = persisted tombstone, findings get deterministic identity.** `FindingKey = (DetectorId, SubjectId)` (subject = the primary entity: productId, recipe line id). Each finding carries a `FactsFingerprint` — a stable hash, computed by its detector, of the facts that make it true (e.g. D1: sorted distinct lot unit ids + display unit id). A finding is suppressed iff a tombstone matches key **and** fingerprint; when facts change the fingerprint differs and the finding reopens with the stale tombstone superseded. Restore deletes the tombstone | The one architectural fork called out in design: dismissal needs identity, but *only* dismissal — so persist the dismissals, not the findings. Reopen-on-fact-change falls out of the fingerprint for free, with detector-local definitions of "what counts as changed" |
| T6 | **Badge count is cached per household** (in-memory, ~10 min TTL), invalidated by dismiss/restore and refreshed whenever the Tidy Up page computes the real list. Layout/More-hub render the badge from the cache only — never runs detectors inline. **REVISED 2026-07-23 (plantry-h0qq, stale-while-revalidate + proactive population):** dogfooding exposed that "briefly stale" in practice meant "absent until you visit Tidy Up, and silently vanishing at TTL expiry" — nothing ever populated the cache proactively. Three additions, none of which put a detector run on a page-render path: **(a) startup warmup** — every process start enumerates all households (unarmed cross-tenant read, `RecipeConversionBackfillCycle` precedent) and requests a background refresh for each, so the badge is correct from the first render after any restart; **(b) SWR cache contract** — `ITidyUpBadgeCache.TryGetAsync` returns `(Count, IsFresh)?`, null only when truly absent; TTL expiry flips `IsFresh` to false instead of evicting the entry, so the badge keeps showing the last known count instead of blinking to nothing; **(c) miss/stale-triggered single-flight refresh** — the layout and More-hub read paths request a background recompute (`TidyUpBadgeRefresher`, a per-household in-flight guard over the existing `IBackgroundTaskQueue`/`QueuedHostedService`, plantry-qll2.4) on every miss or stale read; the queued work item arms tenancy exactly as `RecipeConversionBackfillCycle.RunForHouseholdAsync` does and recomputes through `GetTidyUpPageQuery` — the same single writer of a real count as before, so the badge can never diverge from what the Tidy Up page itself would show. A login-success hook was considered and rejected: the homelab's common cold start is an app restart under a persistent auth cookie, so no login event fires — warmup + SWR already cover every path a login hook would. | The badge is on every page render; running all detectors per request is unacceptable. The page itself stays the truthful source; proactive population just makes the badge honest about it too, without ever moving detector work onto a render path |
| T7 | **Mobile:** no bottom-nav slot (fixed 5 items); Tidy Up appears as a **More-hub tile** with the same count treatment | Matches the existing More-hub pattern for secondary destinations |
| T8 | **v1 shipped detectors D1 + D2** (the conversion-gap family — see §3); D3–D7 shipped as the plantry-i55s follow-up, completing the catalogue | Conversion gaps just demonstrably bit (Onion Yellow), both fix destinations exist today, and the pair exercised the full framework (two contexts, two deep-link shapes, fingerprint semantics) before the remaining five detectors were added |
| T9 | **Dismissal table is household-scoped** with the standard tenancy defense-in-depth (EF filter + RLS, ADR-008) | Findings describe household data; dismissals are household decisions |
| T10 | **CSS/library:** the nav count badge (`.sidebar__count`) is a genuinely reusable primitive → added to `plenish.css` and the Dev component library. Finding rows and group cards are page-scoped compositions of the existing card / row-rhythm / badge / btn primitives | Per the UI-work rules: extract the cross-cutting piece (a count badge on a nav link is plausibly reused), don't catalogue page-specific composition |

## 3. Detector catalogue

The long-term list, so nothing is lost to "we'll think of them later." Severity: **B** = behaviour-affecting
(something is wrong *now* elsewhere in the app), **A** = advisory (a capability silently unavailable).

| ID | Detector | Signal | Consequence (user-facing) | Fix deep link | Phase |
|----|----------|--------|---------------------------|---------------|-------|
| D1 | Stock unit unconvertible to display unit | Any active lot on a product where `Convert(lot.UnitId → DefaultUnitId)` fails | On-hand totals wrong or fall back to lot units; Shopping can misread the product (the Onion Yellow case); low-stock alert can't evaluate | Catalog product detail (conversions) | **v1** · B |
| D2 | Recipe line without a conversion path | Tracked recipe line whose unit has no path to the product's default unit — including ADR-022 deferred gaps AI seeding never resolved | Cooking can't deduct the line from stock; recipe costing incomplete | Recipe editor, anchored to the line | **v1** · B |
| D3 | Expired stock still counted | Active lot with `ExpiryDate` strictly before today — **grace window: 0 days** (agreed with the owner, plantry-i55s): expiring today does not fire, only an already-past expiry | Inflates on-hand; hides the product from shopping suggestions; meal planning assumes usable stock | Pantry product detail (`/Pantry/Products/Detail/{id}`) — **retargeted from Take Stock**: Take Stock has no per-product filter, and the lots grid + per-lot Consume sheet on the product detail page is where an expired lot is actually dealt with | **shipped (plantry-i55s)** · B |
| D4 | Frequent staple with no low-stock alert | Product with `LowStockThreshold = null` purchased on **≥3 distinct `StockEntry.PurchasedAt` dates within the last 90 days** (agreed with the owner, plantry-i55s) — entries with a null `PurchasedAt` don't count; depleted entries do (frequency is about purchase history, not current stock) | Never appears in "Running low" — only surfaces once fully out | Pantry product detail (Set alert) | **shipped (plantry-i55s)** · A |
| D5 | Recipe ingredient with no price data | Product with `TrackStock == true`, referenced by ≥1 recipe ingredient line, with zero price observations — untracked products are excluded (that gap is D7's now; flagging "water has no price" on both would be noise) | Recipe cost-per-serving silently incomplete | Pantry product detail (`/Pantry/Products/Detail/{id}`) — **retargeted (plantry-ep88)**: the original Catalog definition-form target had no price field; the Set-price sheet (plantry-3fqm) lives on the Pantry product page | **shipped (plantry-i55s, retargeted plantry-ep88)** · A |
| D6 | Mixed incompatible units in one product's stock | Active lots in ≥2 units with no mutual conversion (the `DisplayQuantity` "?" fallback case) | Pantry shows quantity as "?"; consumption ordering across lots unreliable | Catalog product detail (conversions) | **shipped (plantry-i55s)** · B |
| D7 | Recipe line uses an untracked product — **redefined (plantry-i55s)**, see note below | Ingredient line whose product resolves with `TrackStock == false` | No stock deduction, no shopping-list integration, no costing for the line. Often *intentional* (spices, "water") — dismissal is the designed answer; smarter suppression heuristics are explicitly out of scope for this bead | Recipe editor, anchored to the line | **shipped (plantry-i55s)** · A |

**D7 redefinition:** the original signal above — "a free-text ingredient line on a recipe, unresolved to
any product" — turned out to be structurally impossible in the current domain model:
`Ingredient.ProductId` is a non-nullable `Guid` (R4/C12), and every persisted ingredient line — typed
against an existing product or inline-created during authoring — always resolves to one. There is no
unlinked state to detect. Agreed with the owner (2026-07-22) to redefine D7 to the closest real gap
instead: an ingredient line whose product resolves with `TrackStock == false` — exactly the lines D2
already skips at its own TrackStock guard.

**Deliberately not in the catalogue:** intake sessions pending review (they have their own surface — the
Upload panel and `/Intake/History`); anything that is a *defect* rather than a data gap (defects get
fixed, not surfaced to users as chores).

## 4. Detection model (Housekeeping.Application)

- **`IProblemDetector`** — `DetectorId Id`, `Severity`, group copy (title + consequence sentence), and
  `DetectAsync(ct) → IReadOnlyList<Finding>`. One implementation per catalogue row, registered in DI;
  the query service discovers them via `IEnumerable<IProblemDetector>` — adding a detector is one class
  + registration, no framework edits.
- **`Finding`** — `(DetectorId, SubjectId, SubjectName, Specifics, Consequence, FixUrl, FactsFingerprint)`.
  Purely a read model; never persisted.
- **`GetTidyUpPageQuery`** — runs all detectors, loads the household's tombstones in one batch, splits
  findings into open (no matching key+fingerprint) and dismissed (matching tombstone → shown under the
  disclosure with its dismissal date), groups by detector, orders groups B-before-A then by detector
  display order, refreshes the badge-count cache (T6).
- **`DismissFindingCommand` / `RestoreFindingCommand`** — write/delete the tombstone
  `(HouseholdId, DetectorId, SubjectId, FactsFingerprint, DismissedAtUtc)`; both invalidate the count
  cache. Dismiss stores the fingerprint *from the finding as rendered*, so a fact change between render
  and click harmlessly re-surfaces the finding.
- **Fingerprint discipline:** the fingerprint covers only facts whose change should reopen the finding
  (D1: the set of unconvertible lot unit ids + the display unit id — not quantities; more of the same
  unit is the same problem). Each detector documents its fingerprint inputs; goldens pin them, since a
  accidental fingerprint change mass-reopens dismissed findings.

## 5. Page & shell (Web)

- **`/TidyUp`** Razor Page per T2/prototype; dismiss/restore are htmx posts swapping the affected group
  card + the disclosure + the nav badge (OOB), same fragment pattern as Shopping suggestions.
- **Layout:** new Manage-band entry after Catalog (broom `<symbol id="i-broom">` from the prototype);
  badge renders from the cached count. More hub gains the tile (T7).
- **Empty state:** `empty-state` primitive, sparkle icon, "All tidy — nothing needs your attention.
  Plantry re-checks as you cook, shop, and scan."

## 6. Open questions (agree before the relevant implementation, per working convention)

1. ~~D3 grace window and D4 "frequent staple" heuristic — numbers to be agreed with the owner at
   follow-up implementation time, not invented in the bead.~~ **Resolved (plantry-i55s, agreed with the
   owner 2026-07-21):** D3 grace window is 0 days (expiring today does not fire, only a past expiry
   does); D4's "frequent staple" is ≥3 distinct purchase dates within the last 90 days. See §3.
2. Badge TTL (design says ~10 min) — tune against homelab feel.
3. Whether D1's fix page needs a "add conversion" anchor/affordance on Catalog product detail, or the
   existing conversions section is discoverable enough once linked.

## 7. Relevant files

- `C:\Users\mgasp\OneDrive\Documents\Claude\Projects\Plantry\code\.preview\data-health-page.html` — approved prototype (all locked UX)
- `src/Plantry.Composition/Shopping/ShoppingPantryReaderAdapter.cs` — the cross-context adapter pattern detectors follow; also where the Onion Yellow "out" was computed
- `src/Plantry.Inventory/Application/InventoryQueries.cs` (`DisplayQuantity`, `SumInDisplayUnit`) — the conversion-failure semantics D1/D6 detect
- `src/Plantry.Recipes/Application/ConversionGapPlanner.cs` + ADR-022 — the entry-time half of D2; Tidy Up is its after-the-fact backstop
- `src/Plantry.Pricing/Application/PricingQueries.cs` (`ProductIdsWithAnyPriceAsync`) + `src/Plantry.Pricing/Domain/IPriceObservationRepository.cs` (`ProductIdsWithAnyObservationAsync`) — the batch price-existence check D5 added (plantry-4t0g convention: no per-product round trip)
- `src/Plantry.Web/Pages/Shared/_Layout.cshtml` — nav bands, icon sprite, More hub entry point
- Beads: `plantry-2hfi` (the originating defect), v1 + follow-up beads filed from this doc
