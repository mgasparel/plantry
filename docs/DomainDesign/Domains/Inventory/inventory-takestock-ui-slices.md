# Take Stock — UI Slices

> **Status:** Design in progress — Phase 4 (bd `plantry-5vxb`)
>
> **Purpose:** Map the journeys onto concrete pages, fragments, and components — **reuse-first** against the Dev component library (`Pages/Dev/Index.cshtml`), per `.claude/CLAUDE.md`. Server-rendered Razor + htmx + Alpine; no SPA/Node. This pass feeds the delivery plan ([PHASE-4-PLAN.md](../../../PHASE-4-PLAN.md)). Builds on the [app services](inventory-takestock-app-services.md) and [journeys](inventory-takestock-journeys.md).
>
> **Bounded context:** Inventory UI in `Plantry.Web/Pages`.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices (← here)
```

---

## Pages & routes

A small Take Stock area. Route group under the Pantry section (where the per-product pages already live).

| Route | Journey | Purpose |
|---|---|---|
| `/pantry/take-stock` | J1 | Landing: the Location list (+ "No location" group when non-empty). The entry point. |
| `/pantry/take-stock/{locationId}` | J2/J3/J4 | The Location walk: count rows, escape hatch, "+ Add item", Save. |
| `/pantry/take-stock/no-location` | J7 | The unassigned-products flow (location picker per row). |

All household-scoped (RLS). Reached from the Pantry page and a nav entry; the Today cold-start CTA (J6) deep-links to `/pantry/take-stock`.

---

## Reuse-first — existing components

Per the component-library rule, **check `Pages/Dev/Index.cshtml` first**. These existing primitives cover most of Take Stock:

| Need | Existing primitive (reuse) |
|---|---|
| Count input (quantity ± ) | The canonical **stepper** (being consolidated under `plantry-izgn.1`) |
| Reason selector (Used it / Spoiled / Correction) | The **segmented control** pattern |
| Product search in "+ Add item" | The **`searchableSelect`** component (the same one the recipe editor's sheet uses) |
| Unit selector (C10) | Standard `<field>` select / `AppendOptions` select pattern |
| Add-item / lot panels | The **sheet** pattern (bottom-sheet/flyout already used by `_AddStockSheet`, `_ConsumeSheet`) |
| Location list, count rows | **card** / list patterns |
| Status chips ("0 / none left", "not stocked") | **pill** pattern |

**No new *shared library* component is warranted** for the count row, the lot escape hatch, or the location list — these are **single-use feature compositions** of existing primitives and stay on the feature pages (per the "don't catalogue page-specific markup" rule).

**The one genuinely shared new component** is the **product-search + inline-create sheet** (C12) — extracted from `Pages/Recipes/Edit.cshtml` into the library so Recipes and Take Stock consume one control. That extraction is its own slice (PHASE-4-PLAN P4-6) and the only library addition this feature proposes.

---

## Client model — the working set (C7)

The per-Location walk holds counts in **Alpine page state only** until Save:

- Each row tracks `recorded`, `counted`, `reason`, and a `dirty` flag (counted ≠ recorded). Visual cue on dirty rows; untouched rows submit nothing.
- **Save** posts the dirty rows (htmx) to the page handler → `SaveCountsCommand`; on success the rows clear their dirty state and the handler returns an updated fragment ("N items updated"). Partial failures (TS-6) re-flag the failed rows with an inline error.
- **"Leave page?" guard** — a `beforeunload` (and htmx-navigation) guard fires when any row is dirty, because unsaved counts are discarded (C7). This is the explicit mitigation for the save-or-lose tradeoff.
- No draft is persisted or resumed (C7) — reloading the page re-reads current stock fresh.

This is the same hypermedia shape as the existing Pantry sheets (Alpine for local state, htmx for the server round-trip), not an SPA.

---

## Per-journey UI notes

- **J1 (landing).** Location cards with an item-count hint; a "No location" card appears only when `ListNoLocationRowsAsync` is non-empty. Empty states: no Locations → prompt to create one; no tracked stock → additive-onboarding framing (J6).
- **J2 (walk).** One row per product: name, recorded count, count input (stepper) with a **unit selector** (C10), a one-tap **"none left" (0)**, an **expand** affordance for multi-lot products, and a row-level **reason selector** that appears once a row goes downward-dirty. A **"+ Add item"** button opens the shared sheet (J5). A sticky **Save** bar shows the dirty count.
- **J3 (escape hatch).** Expanding a row reveals its lots (qty, expiry, open/frozen) using the lot-detail pattern from `_StockDetail`; per-lot quantity inputs, a per-lot "spoiled" toggle (→ `Discarded`), and an "add found stock" affordance with an optional expiry. Collapsing rolls the total back to the scalar row.
- **J4 (save).** The Save bar; confirmation toast; failed rows re-flagged.
- **J5 (add item).** The shared product-search/create sheet, wrapped with count + (implicit current) location fields; on create it calls the write port (`trackStock: true`). Duplicate-name handled inline (search-first).
- **J7 (no location).** Same row shape as J2 but each row carries a **required location picker**; saving sets the default location (TS-9) and writes the opening balance; a row leaves the list once filed.
- **J6 (onboarding from Today).** A cold-start CTA card on Today — a **Home (Today) context change** that composes the existing cold-start machinery and links to `/pantry/take-stock`. Not Inventory UI.

---

## Accessibility / responsive

Follows the existing themed responsive shell and `plenish.css` patterns (the steppers, sheets, and segmented controls already carry their a11y semantics). Count inputs are numeric with the stepper's existing keyboard support; the "leave page?" guard is keyboard/refresh safe.

---

## Open / hand-offs

- Exact stepper/segmented-control markup is whatever the `plantry-izgn` consolidation lands (reuse the consolidated versions, don't fork).
- The shared add-item sheet's extracted API (props for tracked-vs-staple, extra fields slot) is defined in its own slice (P4-6) so both Recipes and Take Stock can consume it.
