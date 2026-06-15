# Meal Planning — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 3 (naming decisions N1–N7 resolved)
>
> **Purpose:** The shared vocabulary for the Meal Planning bounded context. Every term here should
> appear verbatim in domain code, schema, and conversation. Feeds the Domain Model (next step).
> Built from [mealplanning-journeys.md](mealplanning-journeys.md) and aligned with the established
> `DataModels/` and Recipes vocabulary.
>
> **Bounded context:** Meal Planning (`meal_planning` schema, Phase 3). References Recipes,
> Inventory, Pricing, Shopping, Identity **by ID only** (DM-3).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Naming Decisions (resolved ✅)

| # | Choice | Decision | Rationale |
|---|--------|----------|-----------|
| N1 | A filled cell in the week grid (one day × one slot) | **`PlannedMeal`** | "Meal" is the household's word for it; distinct from the recurring **`MealSlot`** *definition*. **Amends ADR-010**'s overloaded "Slot" (which conflated the template and the instance). |
| N2 | One recipe within a meal | **`PlannedDish`** | A meal is composed of *dishes* (main + side); names the multi-recipe shape (C3) without overloading "recipe". |
| N3 | A recurring slot definition in the household config | **`MealSlot`** | The template ("Dinner, attended by both"); held by **`MealSlotConfig`**. Free-text label (C4 / §7h). |
| N4 | The per-member force toward/against a tag | **`Stance`** | FUTURE.md's term; the scale **Required · Preferred · Neutral · Disliked · Restricted** (N5 in Recipes). The owning per-member profile is **`UserPreference`**. |
| N5 | Who eats a given meal | **`Attendee`** / **`AttendeeSet`** | The **effective** attendee set = per-instance override **??** slot default (C5). The planner constrains a meal to its effective attendees. |
| N6 | The AI's staged output before confirmation | **pending suggestions** (a transient store of **`ProposedMeal`**s) | Borrows Intake's `ImportSession` review-then-commit *intent* but **not** its persisted-aggregate shape: suggestions live in a transient, session-keyed store, reviewed **inline** on the plan grid (MP-O7). Entries are **`ProposedMeal`**s carrying a **reasoning** snippet. |
| N7 | The Monday a plan is keyed to | **`WeekStart`** | The ISO-week Monday; `MealPlan` is unique per `(household, WeekStart)` (C2). |
| N8 | What a `PlannedDish` points at | **recipe XOR product** | A dish is a `RecipeId` **or** a `ProductId` (Catalog) — never both (C16). Products = prepared foods + future recipe-output leftovers. Mirrors Shopping's product-or-free-text shape (DM-18). |
| N9 | A meal that isn't a planned meal | **`Note`** | A free-text label on `PlannedMeal` ("Takeout", "Out of town") that **occupies** the slot without dishes (C16). "Note," not "Label," to read as the user's reason rather than a UI tag. |

---

## Aggregates & Entities

| Term | Kind | Definition |
|------|------|------------|
| **MealPlan** | Aggregate root | The household's plan for one ISO week, keyed `(HouseholdId, WeekStart)` — `WeekStart` is the week's Monday (C2). Owns an ordered collection of **PlannedMeal**s. Mutable; **past weeks retained** as the planning-history substrate. |
| **PlannedMeal** | Entity (child of MealPlan) | One meal in the week: a `Date`, the `MealSlot` it fills (by ID), an optional per-instance **AttendeeSet override**, an optional **reasoning** snippet (when AI-proposed), and **either 0..n PlannedDishes XOR a free-text `Note`** (C16). At most one PlannedMeal per `(Date, MealSlot)` (N1); a persisted one is **occupied** (has dishes or a note). |
| **PlannedDish** | Entity (child of PlannedMeal) | One dish planned within a meal: a **`RecipeId` XOR a `ProductId`** (N8/C16), **Servings** (seeded from attendee count, C8), and an **ordinal**. Multiple dishes per meal (main + side, a per-member split from C6, or a recipe + a product). |
| **Note** | Field on PlannedMeal | A free-text reason a slot is occupied without a meal — "Takeout", "Out of town", "Leftovers" (N9). Mutually exclusive with dishes; contributes nothing to fulfillment/cost/shopping. Manual-only (the planner never emits one). |
| **MealSlotConfig** | Aggregate root (one per household) | The household's ordered set of **MealSlot** definitions (§7h). Mutable: add / rename / reorder / archive slots; set per-slot default attendees. |
| **MealSlot** | Entity (child of MealSlotConfig) | A recurring meal definition: free-text **Label** ("Light lunch"), an **ordinal** (order within a day), and **default attendees** (the members who normally eat it, C5). **Soft-archived**, not deleted, so historical PlannedMeals stay resolvable (MP-O2). |
| **UserPreference** | Aggregate root (one per `(household, user)`) | A member's dietary profile: a set of **TagStance** entries. Owned by Meal Planning; references `tag_id` (Recipes vocabulary) and `UserId` (Identity) by ID (C1). |
| **TagStance** | Entity (child of UserPreference) | One member's **Stance** over one `tag_id`. Neutral = the **absence** of an entry. |
| **pending suggestions** | **Transient store** (not an aggregate, MP-O7) | The AI planner's quarantined output for a generation request: validated **ProposedMeal**s held in a **session-keyed, TTL'd server-side store** — never in the `meal_planning` schema and never read by any domain query. The raw payload is validated in memory and **not persisted**. Reviewed **inline** on the plan grid; only confirmed meals write into `MealPlan` (C10, ADR-007). |
| **ProposedMeal** | **Transient DTO** (held in the pending store) | A proposed `(Date, MealSlot)` meal: proposed dishes + a **reasoning** snippet ("Uses chicken that expires Friday"). The pre-confirmation twin of a PlannedMeal; mapped to one via `AssignMeal(source: ai)` on accept. |

---

## Value Objects (computed or transient, not stored unless noted)

| Term | Definition |
|------|------------|
| **Stance** | Enum: **`Required`** (hard positive — only plan recipes carrying this tag) · **`Preferred`** (soft positive — weight toward) · **`Neutral`** (default; absence) · **`Disliked`** (soft negative — weight away) · **`Restricted`** (hard negative — never plan recipes carrying this tag). Polarity + strength live on the member, never on the Tag (FUTURE.md). |
| **AttendeeSet** | The members eating a meal. **Effective** AttendeeSet = `PlannedMeal` override **??** the `MealSlot`'s default attendees (C5). Always intersected with current household membership at read time (J2 edge case). |
| **MealConstraints** | The aggregated constraint set for one meal, derived from its effective attendees' stances: the **union** of hard stances (every `Required` must be met, no `Restricted` tag present) and the **average** of soft stances as a selection bias (C5, MP-O4). |
| **PlanningConstraints** | A generation request's transient inputs (J4 step 1): the **PlanningScope**, **PlanningWeights** (sliders, C14), an optional **budget target** (a soft ceiling, surfaced via the `OverBudget` insight, not a hard cap), and ad-hoc tag prefer/exclude layered atop stored UserPreferences. Held with the pending suggestions in the transient store; never persisted to the plan. |
| **MealFulfillment** | A meal's rolled-up fulfillment at planned servings: **recipe** dishes aggregate the Recipes `FulfillmentResult`; **product** dishes resolve to "in stock for the planned quantity?" directly (Inventory). A note-meal has none. Computed fresh; never stored. |
| **MealCost** | A meal's rolled-up cost: **recipe** dishes sum `CostPerServing × servings` (Recipes); **product** dishes use the product's price × quantity (Pricing). Inherits `CostCompleteness` (`Full`/`Partial`/`None`) — `Partial` if any dish lacks data. A note-meal has none. Deal-blind in Phase 3 (C7). |
| **PlanningScope** | What a generation request targets (C13): **`Week`** (all empty cells) · **`Day(date)`** (a day's slots) · **`SlotSeries(slotId, dates)`** ("all dinners") · **`SingleMeal(date, slotId)`** (one cell, J8). Auto-fill touches only empty cells in scope unless **replace** is requested. The dimension that makes planning a spectrum, not a mode. |
| **PlanningWeights** | The normalized objective weights for a generation (C14): a vector over **PlanningLever** that **always sums to 100** (raise one → others fall proportionally). Bias the planner's **soft** objective only — never relaxes a hard stance (M5). Default leans **Waste**. |
| **PlanningLever** | Enum: **`Cost`** (favour cheaper) · **`Waste`** (favour soon-to-expire stock) · **`Variety`** (avoid recently planned/cooked recipes — reads retained plans + `cook_event`, C2) · **`Deals`** (defined but **fixed at 0 / hidden** until Phase 4, C7). |
| **PlanInsights** | A computed, **advisory** list of **Insight**s over a plan or proposal (C15). Read-side, never stored; recomputed on every change. |
| **Insight** | One advisory observation: an **InsightKind** + a human message (+ optional link/target). |
| **InsightKind** | Enum: **`UnusedExpiring`** (expiring stock the plan doesn't use) · **`OverBudget`** (est. cost > budget target) · **`Repetition`** (recipe repeated this week / from last week) · **`UnfilledSlot`** (requested slot left empty) · **`HardConflictResolved`** (a meal was split into separate dishes, C6). |

---

## Stance, attendance & the planner (the heart of the context)

Tags are a **controlled, shared household vocabulary owned by Recipes** (recipes C2 / O3); Meal
Planning **reads them by ID** and attaches per-member **Stance** to them. The planner's core move:

1. For each meal, resolve the **effective AttendeeSet** (override ?? slot default, C5).
2. Aggregate those attendees' stances into **MealConstraints**: union the hard ones, average the soft ones.
3. Choose dishes that satisfy the hard constraints and maximize the soft score + fulfillment + expiry-use − cost.
4. When attendees' **hard** stances are irreconcilable (one `Vegan`-`Required`, one meat-`Required`), **split into separate `PlannedDish`es** — one per conflicting requirement — within the same meal (C6).

This is why *per-member preferences*, *per-slot attendance*, and *multi-dish meals* are one
mechanism, not three features: attendance scopes whose stances apply, and multi-dish is how
conflicting hard stances are honoured.

**The planner's output is advisory (C12).** Hard stances bind the **AI's own** proposals (the ACL
validates them), but never the user: every proposed meal can be swapped, hand-edited, cleared, or
rescheduled, at review and after acceptance. A planned meal is editable the same way no matter how it
arrived — generated, auto-filled at any **PlanningScope**, or hand-assigned. Planning is a
**spectrum** from fully manual to fully automatic, mixable within one plan (C13).

---

## Domain Events

| Event | Payload | Emitted when |
|-------|---------|--------------|
| **MealPlanned** | `householdId, weekStart, date, slotId, source` (`manual` \| `ai`)`, by, at` | A meal is saved into the plan — manually assigned/edited (J5, `source: manual`) **or** an AI suggestion accepted (J4, `source: ai`). |
| **MealMoved** | `householdId, weekStart, fromDate, fromSlotId, toDate, toSlotId, by, at` | A meal is rescheduled by drag-and-drop (J9); on a swap, two `MealMoved`s. |

> Kept deliberately light (as Recipes did). No Phase-3 cross-context reaction depends on these —
> Shopping is called directly via a port (J6) — so they exist for audit/attribution and future
> consumers (e.g. analytics), not to drive behaviour.

---

## Key Actions (verbs)

| Verb | Meaning |
|------|---------|
| **Generate** / **Auto-fill** | Run the untrusted planner over gathered context + per-meal MealConstraints, for a given **PlanningScope** → validated **pending suggestions** rendered as ghost cells (J4). |
| **Propose** | The planner's act of placing dishes into ProposedMeals with reasoning (within Generate). |
| **Review** | Work the suggestions **inline** on the plan grid: accept all / accept or reject a cell / swap / regenerate a meal / hand-edit (J4, J8). |
| **Accept** | Commit a confirmed suggestion into the `MealPlan` via `AssignMeal(source: ai)` — per cell or accept-all (J4). |
| **Assign** | Manually place recipe(s) as dishes into a `(date, slot)` meal (J5). |
| **Move** / **Reschedule** | Drag a `PlannedMeal` to another cell — relocate onto an empty cell, swap onto an occupied one (J9). |
| **Tune weights** | Adjust the `PlanningWeights` sliders (Cost / Waste / Variety) for a generation (J4, C14). |
| **Surface insights** | Compute and show `PlanInsights` over a plan/proposal (J10, C15). |
| **Attend** | A member's participation in a meal; sets whose stances constrain it (C5). |
| **Shop for the week** | Roll up all `Missing` ingredients across the week's dishes and hand them to Shopping (J6). |
| **Configure slots** | Add/rename/reorder/archive `MealSlot`s and set default attendees (J2). |
| **Set preferences** | Edit a member's `TagStance`s (J3). |

---

## Cross-context terms (owned elsewhere, referenced by ID)

These are **not** redefined here — this fixes which word Meal Planning uses for each.

| Term | Owning context | Note |
|------|----------------|------|
| **Recipe** | Recipes (DM-20) | A `PlannedDish`/`ProposedMeal` references `recipe_id`; never an embedded recipe. |
| **Product** | Catalog (DM-10) | A `PlannedDish` may reference a `product_id` instead of a recipe (C16) — prepared foods, and the future **recipe-output** product kind (FUTURE.md). Read for the product's name, stock, and price. |
| **Tag** / **tag category** | Recipes (C2 / DM-20) | The vocabulary `Stance` attaches to. Kind-less; Stance lives here, on the member. |
| **FulfillmentResult** / **IngredientStatus** | Recipes (read model) | Rolled up into `MealFulfillment`; never recomputed here. |
| **CostPerServing** / **CostCompleteness** | Recipes (read model) | Rolled up into `MealCost`; deal-blind in P3 (C7). |
| **ServingsScale** | Recipes | The ratio applied for fulfillment/cost/consume; `PlannedDish.Servings` materializes it. |
| **ProductStock** / **expiry** / **FEFO** | Inventory (DM-13) | Read for prefer-expiring and fulfillment. |
| **Consume** | Inventory (ADR-011) | **Not called by planning** — cooking a planned meal is the Recipes Cook flow (J4 of Recipes). Meal Planning plans; Recipes cooks. |
| **CookEvent** / cook history | Recipes (DM-20) | Read by the **Variety** lever (C14) — "don't plan what we recently cooked." Paired with this context's own retained plan history (C2). |
| **PriceObservation** | Pricing (DM-17) | Feeds `MealCost` (purchase-price only in P3). |
| **ShoppingList** / **AddItems** | Shopping (DM-18) | Target of "shop for this week" (J6), reusing the P2-4 seam. |
| **Household** / **User** / **membership** | Identity (DM-6) | Tenancy, attendees, and per-member preference owners. |
| **AI / household AI key** | per ADR-007 / DM-7 | The planner is an untrusted function; the key is encrypted at rest, never client-sent. |
| **Deal** | Deals (Phase 4) | **Not referenced in Phase 3** — the deal-aware seam is left open (C7). |

---

## Reconciliation notes

- **ADR-010 "Slot" → `MealSlot` + `PlannedMeal` + `PlannedDish`.** ADR-010 modelled "`MealPlan`
  root (week + `Slot` children, each referencing a `recipe_id` and a meal-slot label)." This is
  refined: the *recurring definition* is a **`MealSlot`** (in `MealSlotConfig`), the *instance* is
  a **`PlannedMeal`**, and a meal holds **multiple `PlannedDish`es** (C3) rather than one
  `recipe_id`. The label lives on `MealSlot`; a `PlannedMeal` references it by ID, not by label
  string. To be recorded as an ADR-010 amendment when the domain model lands.
- **`UserPreference` graduates from FUTURE.md into Phase 3.** FUTURE.md modelled the Stance scale
  and deferred it "until the meal planner exists." It exists now (C1); the Tag was built kind-less
  in Recipes precisely so this needs **no tag migration**.
