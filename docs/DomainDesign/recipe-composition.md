# Recipe Composition (Inclusions) & Yield-on-Cook

**Status:** Accepted design · **Author:** design conversation (mgasparel + Claude) · **Date:** 2026-07-09
**Scope:** Two orthogonal features: (F2) *recipe composition* — a recipe includes N servings of another
recipe as a line item; (F1) *yield-on-cook* — a cooked recipe can add leftover/prepped servings to
inventory as a catalog product. F2 is the primary subject; F1 is specified in §9 as an independent track.
**Out of scope (deliberately deferred):** the F1×F2 intersection ("use prepped stock instead of making
fresh" at cook time), inline sub-recipe directions rendering, tag/cook-time propagation to browse
filters, a first-class "prep recipe" concept (§11).

---

## 1. Motivation

Shared bases — Vegan Nacho Cheese, Caesar Salad Base, Pie Crust, Nacho Chili — are re-typed in full
in every recipe that uses them (the Grocy import *flattened* 16 nesting edges for exactly this reason,
tradeoff T14). The driver is **authoring reuse**: one canonical sub-recipe, edits propagate, parents
declare "2 servings of Nacho Cheese" as a line.

Two real-world behaviours hide under "nested recipes":

- **Made-fresh composition** — the sub is cooked *as part of* the parent (Caesar base under a protein).
  This is F2: at cook time the sub's ingredients are consumed as raw products.
- **Batch prep / leftovers** — the sub exists in the fridge with a quantity and use-by date.
  This is F1: cooking adds yield to inventory; parents (or people) consume it later.

Modelling them as one mechanism (Grocy's approach) produces the hybrid complexity both features
individually avoid. They are built separately; their intersection is a future cook-time affordance.

## 2. Decision summary

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | `Inclusion` is a **sibling line type** next to `Ingredient`, not a union-typed ingredient | Ingredient invariants R4–R6 and every existing consumer stay untouched; consumers opt in explicitly |
| D2 | Inclusion amount is denominated in **servings of the sub-recipe** (decimal > 0) | Matches Grocy source data (direct import); invariant under *proportional* rescale of the sub — per-serving content is unchanged, so parents never silently change. Batch fraction would double on a 4→8 rescale. UI may render the batch fraction as a hint ("2 servings · ½ batch") |
| D3 | R3 relaxes to **≥ 1 ingredient OR inclusion** | Assembly recipes ("Caesar Deluxe" = base + cauliflower, zero direct ingredients) are explicitly wanted |
| D4 | One **`RecipeExpansionService` choke point** resolves a recipe to a flat product-level line list with provenance | `CookRecipe`, shortfall, shopping, costing consume the expanded view; none individually learn recursion. Downstream of expansion everything stays product-flat — the single consumption primitive is preserved |
| D5 | Recursive nesting allowed, **no depth cap**; **cycles rejected at save** (application layer, household-scoped graph walk) | Household-scale recipe counts make the walk trivially cheap; a cap is arbitrary. Cross-aggregate validation in the app layer follows the R1 name-uniqueness precedent |
| D6 | **Full per-line toolkit at cook time** (skip/swap/variant-split) on expanded lines, keyed by **path-qualified identity** (§6) | Reflects reality ("out of nutritional yeast → sub miso inside the nacho cheese"); the sub's `IngredientId`s alone are not unique once a sub can appear twice in a tree |
| D7 | Whole-inclusion **skip** in addition to per-line controls | "Not making the cheese tonight" is one tap, not N skips |
| D8 | `CookConsumeLine` gains **`SourceRecipeId`** provenance (nullable soft ref) | Cook history must render "miso paste — in Nacho Cheese"; the sub's IngredientId can't be resolved against the parent recipe |
| D9 | Diet-tag nudge evaluates the **expanded** product set; dismissal hash becomes the expanded-set hash | User decision: the nudge should catch a dairy ingredient inside a sub of a Vegan-tagged parent |
| D10 | **Reverse ripple**: saving a sub re-runs the cheap nudge guard for diet-tagged includers; surfaced on the sub's save landing | A sub edit changes parents' effective ingredients with no parent save to trigger on |
| D11 | Editor affordance is a **separate "Include a recipe" action** backed by the existing recipe-search JSON endpoint | No mixed product+recipe picker exists (meal planner's `DishSearch` is recipe-only); `ProductSearchCreateSheet` stays untouched |
| D12 | **Archiving a recipe included by others is blocked** with a "used by N recipes" message | Warn-and-block is the least surprising v1 rule; auto-flatten-on-archive can come later |
| D13 | Proportional rescale of the **parent** scales inclusion servings with the ratio; **fixed-mode** rescale of a **sub** triggers a warning nudge naming/counting includers | Parent scaling must scale all lines uniformly; fixed-mode sub rescale changes what a serving means, silently changing parents |
| D14 | The same sub **may appear more than once** in a parent (e.g. Pie Crust ×1 "Base", ×0.5 "Lattice") | Trivial to allow; shortfall/shopping aggregate by product so nothing double-counts |
| D15 | Directions: parent pages **link** to the sub-recipe (v1); inline expandable rendering is a later prototype | Render-only concern; don't block the domain work on it |
| D16 | The 16 Grocy nesting edges **re-import as inclusions** (amount is already servings) | Retroactively redeems import tradeoff T14; un-denormalizes flattened bases |

## 3. Domain model

New child entity on the `Recipe` aggregate, wholesale-replaced with the line set on edit (mirrors O1):

```
Inclusion
  Id            InclusionId          (re-minted per save, like IngredientId)
  HouseholdId   HouseholdId
  RecipeId      RecipeId             (owner / parent)
  SubRecipeId   RecipeId             (the included recipe — same household)
  Servings      decimal              (> 0; servings of the sub-recipe)
  GroupHeading  string?              (shares the ingredient group-heading convention)
  Ordinal       int                  (SHARED ordinal space with Ingredient lines)
```

Invariants (new rule ids, continuing the recipes-domain-model numbering style):

- **N1** — `Servings > 0`.
- **N2** — `SubRecipeId ≠ RecipeId` (no self-inclusion; degenerate cycle).
- **N3** — ordinals are contiguous across the **union** of ingredient and inclusion lines (R6 widens).
- **N4** *(application layer)* — the inclusion graph is a DAG: on save, walk the household's inclusion
  edges from each `SubRecipeId`; reject if the saved recipe is reachable. Same-household reference and
  sub-existence are checked here too.
- **N5** *(application layer)* — a recipe referenced by ≥ 1 inclusion cannot be archived (D12).
- **R3′** — a recipe must have ≥ 1 ingredient **or** inclusion (D3).

`ReplaceIngredients` generalizes to `ReplaceLines(ingredients, inclusions)` (one wholesale replace,
one `RecipeUpdatedEvent`). `ChangeDefaultServings(Proportional)` multiplies `Inclusion.Servings` by the
ratio alongside ingredient quantities (D13).

## 4. Expansion semantics (`RecipeExpansionService`)

For an inclusion of `S` servings of sub `R` with `R.DefaultServings = D`, the **factor** is `f = S / D`.
Expansion of a recipe yields a flat list of:

```
ExpandedLine
  Path          InclusionId[]        (empty for the recipe's own direct ingredients)
  IngredientId  IngredientId         (the ingredient's id in ITS OWN recipe)
  SourceRecipeId RecipeId            (the recipe the ingredient physically belongs to)
  ProductId     Guid
  Quantity      decimal?             (source qty × product of factors along Path, 3-dp rounded; null stays null)
  UnitId        Guid?
  GroupPath     string[]             (inclusion display names + the source line's GroupHeading, for rendering)
```

- Recursive: nested inclusions multiply factors along the path.
- Untracked staples (null qty/unit) pass through untouched — C12 applies downstream as today.
- Expansion is a **read-time** operation; nothing expanded is ever persisted on the Recipe side.
- The DAG invariant (N4) guarantees termination; the service still carries a defensive visited-set.

**Line identity:** `Path + IngredientId` is the unique key for one expanded line (D6). Serialized for
form fields as the `/`-joined GUID path (empty path = direct ingredient, preserving today's shape).

## 5. Editor & display (Web)

- **Add**: an "Include a recipe" action beside the add-ingredient affordance opens a recipe search
  (reuse the meal-planner `SearchJson` endpoint pattern; exclude self client-side — N4 at save is
  authoritative). Picking one creates an inclusion line with a servings stepper and the batch-fraction
  hint (D2).
- **Line rendering** (editor + Details): inclusion lines sit in ordinal position, titled
  "2 servings · Vegan Nacho Cheese", linking to the sub (D15), with an expandable read-only preview of
  the expanded ingredients.
- Directions: link only in v1 (D15).

## 6. Cook flow

- The cook page renders direct lines as today, plus one **group per inclusion** ("Nacho Cheese —
  2 servings", scaled by the parent's `ServingsScale`) containing its expanded lines with the full
  per-line toolkit, and a whole-inclusion skip at the group header (D6/D7).
- `IngredientResolution` keying moves from bare `IngredientId` to the **path-qualified key** (§4).
  Direct ingredients keep an empty path — existing call sites map 1:1.
- `CookConsumeLine` gains `SourceRecipeId` (D8; migration). The ad-hoc `Guid.Empty` ingredient sentinel
  is unchanged. Idempotency/reconciliation continue to ride on the line's own Id — no change to the
  anchor-first protocol, the reconciler, or deferred-unit-gap handling (they key on ProductId / line Id,
  never on ingredient identity).
- Consume targets are built from the **expanded** line list; everything downstream of target-building
  (TrackStock batch resolution, C12 skips, Pending → Applied/Shorted/DeferredUnitGap) is untouched.

## 7. Shortfall, shopping list, costing

`RecipeShortfallCalculator`, `AddIngredientsToShoppingList` / `AddMissingToShoppingList`, and
`CostingService` consume the expanded line list via the choke point (D4), aggregating by
`(ProductId, UnitId)` so duplicate subs (D14) merge naturally. Sub-recipe cost is by construction its
expanded ingredient cost × factor.

## 8. Diet-tag nudge over the expanded set

- The **cheap post-save trigger** compares the hash of the **expanded** distinct ProductId set (one
  extra repo query to fetch included recipes recursively — still no LLM, no catalog name resolution;
  "most saves trigger nothing" survives). `DietNudgeDismissedHash` stores the expanded-set hash.
  The in-aggregate `CurrentIngredientProductHash` remains for the direct set; the expanded variant
  lives with the expansion service (it needs cross-aggregate reads).
- **Reverse ripple (D10)**: after saving a recipe that is included by others, reverse-lookup includers
  (transitively), run the same cheap guard for each diet-tagged parent, and surface contradictions on
  the *sub's* save landing ("this change may conflict with 'Vegan' on Nachos"). Gate and dismissal
  semantics are per-parent, identical to the direct nudge.

## 9. Feature 1 — Yield-on-cook (independent track)

A recipe may declare a **yield**: `YieldProductId` (ordinary catalog product, auto-creatable from the
recipe name when first enabled), `YieldUnitId` (defaults to a servings-like count unit; a real unit —
cups — is allowed for prep-style recipes). At cook confirmation the user states how much is being
**stored** ("cooked 4, eating 2, storing 2") and an inventory **add** of the yield product lands (lot
with user-supplied expiry) via an `IInventoryProducer` counterpart to `IInventoryConsumer`. Eaten-now
portions need no inventory action; eating leftovers later is an ordinary inventory consume.
The produce step joins the anchor-first protocol as an additional pending line kind.
The 21 Grocy "produces product" links (import tradeoff T12) remain in the manifest and can back-fill
yields. The F1×F2 intersection — offering prepped stock in place of a made-fresh expansion — is
explicitly deferred (§11).

## 10. Grocy re-import of nestings

The 16 nesting edges (flattened at import, T14) re-import as inclusions: Grocy nesting `amount` is
already servings of the sub (D2 — direct copy), mapped through the existing recipe crosswalk. Only
edges whose parent *and* sub both committed are importable. **De-flatten rule (decided):** recompute
the flattened staging output from the manifest and compare its (ProductId, Quantity, UnitId,
GroupHeading) multiset to the parent's current lines. Equal (untouched since import) → wholesale-replace
with the non-flattened projection (direct ingredients + inclusions). Not equal (user edited since
import) → skip and report; never merge into edited recipes. Idempotent by construction — a converted
parent no longer matches the flatten output and reports as already-converted.

## 11. Deferred / open items

1. **F1×F2 intersection** — cook-time "use 1 cup from the fridge instead of making fresh".
2. **Inline sub-recipe directions** on the parent's cook page (prototype after v1's link).
3. **Prep-recipe concept** — "included-by N" is derivable once inclusions exist; planner demotion or a
   "Prep" grouping needs no schema change. Tags cover the interim.
4. **Tag/cook-time propagation** to browse filters (a Vegan filter does not inspect subs in v1).
5. **Costing display** for yield products (unit cost = batch ingredient cost ÷ yield).

## 12. Work breakdown

Epic `plantry-fqb0`, children in dependency order (F2): ① domain (Inclusion, R3′, N1–N5, rescale
semantics; backend only — the D13 fixed-mode warning UI belongs to ③) → ② `RecipeExpansionService` →
③ editor/Details UI (incl. D13 warning) → ④ cook **command** (path-keyed resolutions, provenance,
existing page kept working via empty-path keys) → ⑨ cook **page UI** (grouped rendering,
whole-inclusion skip, provenance display) → ⑤ shortfall/shopping/costing via expansion → ⑥ diet nudge
expanded hash → ⑦ reverse ripple → ⑧ Grocy nesting re-import (de-flatten rule, §10). F1
(yield-on-cook) is the separate feature issue `plantry-854a`. Each issue carries acceptance criteria
and file-level design notes for autonomous workers.
