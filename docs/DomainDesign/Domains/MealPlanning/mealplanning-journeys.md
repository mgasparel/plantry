# Meal Planning — User Journey Map

> **Status:** Complete — Phase 3. Fed the [ubiquitous language](mealplanning-ubiquitous-language.md)
> and [domain model](mealplanning-domain-model.md); all open decisions below are now resolved. Next
> pipeline stage: the Data Schema pass.
>
> **Purpose:** Checkpoint of the user-journey-mapping session for the Meal Planning bounded
> context (Phase 3 = the AI meal planner). Feeds the ubiquitous language, domain model, and data
> schema (next steps). Anchored on [SPEC.md](../../../SPEC.md) §5 (Meal Plan) and §7h (Meal
> Slots), [ADR-010](../../../ADRs/ADR-010.md) (Meal Planning aggregates), and the tag-driven
> planning / `UserPreference` design in [FUTURE.md](../../../FUTURE.md).

---

## DDD Process

```
User Journeys (← here)  →  Ubiquitous Language  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Confirmed Decisions

| # | Decision | Outcome |
|---|----------|---------|
| C1 | Per-member dietary preferences | **Built in Phase 3.** A persistent `UserPreference` profile per household member, holding a **Stance** (`Required` · `Preferred` · `Neutral` · `Disliked` · `Restricted`) over each `tag_id`. Owned by Meal Planning; reads the Recipes-owned tag vocabulary by ID (FUTURE.md graduates here). Neutral = absence of a stance. |
| C2 | Plan time model | **MealPlan keyed by ISO week** (`week_start` = the week's Monday) per household. **Past weeks are retained** — the substrate for future "what did we eat" analytics, mirroring `cook_event`. "Current week" is derived from the clock; "plan next week" is just a plan with next Monday's `week_start`. |
| C3 | Recipes per meal | **A meal holds 0..n dishes** as `PlannedDish` entries (main + side = two dishes). Not a single `recipe_id` — this **amends ADR-010**'s "Slot … referencing a recipe_id". *(Extended by C16: a dish is a recipe **or** a product.)* |
| C4 | Meal slots | **User-configurable, free-text, ordered** per household (§7h): e.g. "Breakfast", "Light lunch", "Mid-day snack", "Dinner". A household that only plans dinners configures a single slot. Backed by `MealSlotConfig` (ADR-010). |
| C5 | Meal attendance | **Per-slot attendance.** Each `MealSlot` carries **default attendees** (which members normally eat it); a specific `PlannedMeal` may **override** (nullable — inherits the slot default when unset) for guests / one-offs. The planner constrains each meal to its **effective attendees'** preferences only. |
| C6 | Irreconcilable hard preferences at a shared meal | **Auto-plan separate dishes (committed Phase-3 scope).** When attendees' hard stances conflict (one `Vegan`-`Required`, one meat-`Required`), the planner emits **multiple `PlannedDish`es** — one satisfying each — within the one meal, explains the split in the reasoning snippet, and surfaces it as a `HardConflictResolved` insight (C15). This is the reason multi-dish meals exist; it **graduates from FUTURE.md into committed Phase-3 work** (the current ACL is a simplified interim — no generative per-attendee split yet). |
| C7 | Deal-awareness | **Deferred to Phase 5.** Deals moved to Phase 5, so the Phase-3 planner is **deal-blind**: cost comes from purchase-price history (Pricing, DM-17) only. The "active deals" inputs in SPEC §5b step 3 and the deal-aware bias are a Phase-5 enhancement; the seam is left open, not built. |
| C8 | Servings | **Effective attendee count seeds default servings** for a planned/proposed dish; the user can override. Servings then flow through the existing Recipes `ServingsScale` for fulfillment, cost, and consume. |
| C9 | Hard-preference enforcement | **AI proposals must satisfy** every effective attendee's hard stances (`Required` / `Restricted`) — the planner filters before proposing (C10 ACL validates the AI's output too). **Manual assignment warns but does not block** (minimum-friction principle, mirroring the Recipes cook flow): the user may knowingly plan a meal that violates a stance. |
| C10 | AI as untrusted function | The planner is an **untrusted function** (ADR-007). Its raw output is validated in a **transient ACL step** and held as **pending suggestions** in a session-keyed store (quarantined — never in the schema, never read by a domain query); only **user-confirmed** meals cross into `MealPlan`. Same review-then-commit *intent* as Intake, but a transient store reviewed **inline** rather than a persisted staging aggregate (MP-O7, ADR-010 amendment). |
| C11 | Rescheduling meals | **Drag-and-drop a `PlannedMeal` between cells** (J9) — the intuitive calendar gesture for "let's eat the pizza tonight instead of tomorrow." A cell holds an **ordered stack** of meals, so a drop **relocates** the meal into the target cell (appended to its stack); it never swaps or displaces what's already there. A meal's per-instance **attendance override travels with it**; a meal with no override **inherits the destination slot's default attendees**. Scoped to **within one week** (one `MealPlan`) in v1. |
| C12 | Suggestions are advisory, never binding | An AI plan is a set of **suggestions**. The **user is always authoritative** — they can override any proposed meal at review (swap / regenerate / hand-edit inline, J4 step 8) **and** after it is saved (edit / replace / clear / reschedule any meal). The planner never locks a decision; it proposes, the human disposes. This is the meal-planning expression of the minimum-friction principle. |
| C13 | Planning granularity (manual ↔ automatic spectrum) | **Fully flexible and mixable.** The household can plan **every meal by hand** (J5), **auto-generate the whole week** (J4), or **auto-fill a chosen scope** — a single **day** (all its slots), a **slot across days** ("auto-fill all dinners"), or **one meal** (J8). Manual and AI-chosen meals coexist in the same plan; auto-fill only touches **empty** cells unless the user explicitly asks to replace existing ones. |
| C14 | Generation weighting (sliders) | The single "prefer expiring" toggle becomes a set of **weighted levers** — **`PlanningWeights`** — that **always sum to 100**: pushing one up proportionally lowers the others. Phase-3 levers: **Cost** (favour cheaper), **Waste** (favour using soon-to-expire stock — the old toggle at max), **Variety** (avoid repeating recent recipes — reads retained plan history + Recipes `cook_event`, C2). **Deals** is a defined lever but **fixed at 0 / hidden** until Phase 5 (C7). Two rules: weights bias **soft optimization only** — they **never** relax a hard stance (an allergy is not a slider); and the optional **budget target** (SPEC §5b) is a **separate soft ceiling**, complementary to the Cost lever, surfaced via an over-budget insight (C15). Default distribution leans **Waste** (the VISION pillar), user-adjustable. |
| C15 | Plan insights (advisory callouts) | A computed, **advisory** insights panel visible **during review and on a saved plan** (J10): e.g. **`UnusedExpiring`** ("Chicken breast expires in 3 days, but isn't used in this plan"), **`OverBudget`**, **`Repetition`** (same recipe this week / repeated from last week), **`UnfilledSlot`**, **`HardConflictResolved`** (a meal was split into separate dishes). Read-side only — observations, never blocks (C12) — computed over the plan + Inventory expiring stock + Recipes reads; **no new ports**. |
| C16 | Meal-slot content (unified) | A **`PlannedDish`** references a **recipe XOR a product** (Catalog) — products cover prepared foods ("frozen pizza") and future recipe-output leftovers; a **`PlannedMeal`** is **dishes XOR a free-text `Note`** ("Takeout", "Out of town"). Two-level XOR; reuses the DM-18 product-or-free-text pattern. **Occupied = has dishes or a note** → auto-fill skips it like any recipe slot, replaced only on explicit request. **Products and Notes are manual-only in v1** — the AI planner proposes **recipes** (proposing in-stock products is a natural future extension of the Waste lever). A product is fully stock/price-computable, so fulfillment/cost/shop "just work"; a note contributes nothing by construction. **Deferred:** the product-meal **eat→consume** action (recipe-meals consume via the existing Recipes Cook flow; product-meals' consume rides with the future recipe-output feature, [FUTURE.md](../../../FUTURE.md)). Supersedes C3's "recipe-only" reading and the earlier Q3 call. |

---

## Open Decisions — all resolved ✅

These were the questions left open by the journey-mapping session; **all were resolved in the
[domain-model pass](mealplanning-domain-model.md)** (see its "Open decisions resolved" section for the
rationale of each).

| # | Decision | Context | Resolution |
|---|----------|---------|------------|
| MP-O1 | `UserPreference` aggregate granularity (per-user profile vs per-edge rows) | C1 | Per-`(household, user)` profile root owning `TagStance` children. |
| MP-O2 | `MealSlot` identity stability across config edits; deleting a slot that has planned meals | C4 | Stable `MealSlotId`; slots **soft-archived**, never hard-deleted, so historical `PlannedMeal`s stay resolvable. |
| MP-O3 | Generation scope — whole-week vs day vs slot-series vs single-meal auto-fill | C10, C13, J4/J8 | A `PlanningScope` parameter (`Week` / `Day` / `SingleMeal`); review is inline on the grid, no separate page. |
| MP-O4 | Where the hard/soft aggregation across attendees lives (domain service vs AI prompt only) | C5, C6, C9 | A domain service (`MealConstraintResolver`) — the pure heart of the planner, not the prompt. |
| MP-O5 | Weights, budget, and the hard/soft boundary | C14 | `PlanningWeights` (sum 100) bias soft optimization only; hard stances sit outside it; budget is a soft ceiling surfaced as an insight. |
| MP-O6 | Slot content — recipe / product / note | C16 | Two-level XOR: a `PlannedDish` is a recipe **XOR** product; a `PlannedMeal` is dishes **XOR** a free-text `Note`. |
| MP-O7 | AI staging shape — persisted proposal aggregate vs transient inline store | C10, C12, J4/J8 | A **transient, session-keyed pending store**, reviewed inline — not a persisted aggregate (deliberate divergence from Intake). |

---

## Journeys

### J1 — View the current week's meal plan

**Trigger:** User opens Meal Plan (via the More tab / nav).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Resolves the current ISO week from the clock; loads (or lazily creates an empty) `MealPlan` for `(household, week_start)`. |
| 2 | System | Renders a **weekly calendar**: one column per day, one row per configured `MealSlot` (§7h order). Each cell is a `PlannedMeal`. |
| 3 | System | For each `PlannedMeal`, renders its dishes (recipe **or** product names) **or** its free-text **Note** chip (C16), the meal's rolled-up **fulfillment %** and **estimated cost** (sum across dishes at planned servings; a note-meal shows neither), and its **attendees**. |
| 4 | System | Flags any planned dish whose recipe has an ingredient expiring ≤ 4 days ("Use soon"), reusing the Recipes fulfillment read model. |
| 5 | System | Renders the **advisory insights panel** (`PlanInsights`, C15 / J10) for the week — unused expiring stock, over-budget, repeated recipes, unfilled slots. |
| 6 | User | Navigates to previous / next week (J7); the grid reloads for that `week_start`. |

**Domain events emitted:** none (pure query).

**Edge cases:**
- No `MealSlotConfig` slots configured yet → empty grid with a prompt to configure slots (J2).
- A planned dish references a recipe or product later archived in Recipes/Catalog → cell shows the dish as "(removed)"; cost/fulfillment for that dish omitted (the plan record is retained, like cook history).
- Empty cell → "+" affordance to assign (J5) or "Generate" (J4). Filled cell → an **"Add meal"** affordance to stack another meal in the slot (J5).

---

### J2 — Configure meal slots & attendance

**Trigger:** User opens Settings → Meal Slots (§7h), or the "configure slots" prompt from an empty plan.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Loads the household's `MealSlotConfig` (seeded with a sensible default — Breakfast / Lunch / Dinner — at household creation). |
| 2 | User | Adds a slot with a free-text **label** ("Mid-day snack"), reorders slots (drag / up-down), renames, or removes one. |
| 3 | User | Sets each slot's **default attendees** from the household roster — e.g. "Lunch → just me", "Dinner → both of us". |
| 4 | System | Validates: labels non-blank and unique per household; ordinals contiguous; default attendees are current household members. |
| 5 | System | Persists `MealSlotConfig`. Future plans use the new slot set and attendee defaults; existing planned meals keep their recorded slot reference (MP-O2). |

**Domain events emitted:** none (configuration).

**Edge cases:**
- Removing a slot that has planned meals in past/current weeks → slot is **soft-archived**, not hard-deleted, so historical `PlannedMeal`s stay resolvable (MP-O2); it disappears from the grid for future weeks.
- A default attendee is later removed from the household → resolved attendee set silently drops them (membership is the source of truth); no orphaned reference.
- Reducing to a single "Dinner" slot → grid collapses to one row per day.

---

### J3 — Set my dietary preferences

**Trigger:** User opens their preference profile (per-member, from Account/Settings).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Loads the member's `UserPreference` — their **Stance** over each household `tag_id`. Lists tags grouped by the tag's cosmetic `category` (Diet / Protein / Flavor / Cuisine) for scannability. |
| 2 | User | Sets a Stance per tag on the scale **Required · Preferred · Neutral · Disliked · Restricted** (Neutral = no stance / default). E.g. a vegan sets `Vegan = Required`; a nut allergy sets `Tree Nuts = Restricted`; "I love spicy" → `Spicy = Preferred`. |
| 3 | System | Validates one Stance per `(user, tag)`; Neutral persists as the **absence** of a row. |
| 4 | System | Persists the profile. Future plan generations for any meal **this member attends** apply these stances (J4). |

**Domain events emitted:** none (preference state is read by the planner).

**Edge cases:**
- A tag is renamed in Recipes → the stance follows automatically (it references `tag_id`, not the label).
- A tag is deleted in Recipes → any stance over it is dropped (no orphan); the planner simply has one fewer constraint.
- Two members set conflicting hard stances on the same tag → not a conflict *here* (it's per-member); it only matters when both **attend the same meal** (C6 / J4 step 5).

---

### J4 — Generate a meal plan with AI *(the hero — one screen, inline)*

**Trigger:** User is on the Meal Plan week view (blank, partly filled, or full) and taps **Auto-fill
week**, or a day column's **Auto-fill day**. There is **no separate proposal screen** — suggestions
appear in place (MP-O7). *(Full flow: the steps below; the pending-suggestion state model lives in the
[domain model §6](mealplanning-domain-model.md#6-the-ai-acl--a-transient-pending-store-not-an-aggregate-mp-o7).)*

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | User | Picks the **scope** via which button they tap (`PlanningScope`: `Week` / `Day`; per-cell regenerate is `SingleMeal`, J8). Optionally first tunes the **weighting sliders** (`PlanningWeights`, C14 — Cost / Waste / Variety, summing to 100, default leans Waste), sets a **budget target**, and adds ad-hoc tag prefer/exclude **on top of** stored member preferences. Auto-fill touches only **empty** cells in scope unless the user asks to replace existing meals (C12/C13). |
| 2 | System | Gathers context **server-side**: pantry + expiring stock (Inventory); each candidate recipe's tags + live fulfillment + cost (Recipes read models); the configured slots, their **effective attendees**, and **those attendees' stances** (C5). |
| 3 | System | For each target meal, computes the **effective constraint set**: union the hard stances (`Required` / `Restricted`); average the soft stances (`Preferred` / `Disliked`) into a selection bias (MP-O4). |
| 4 | System | Invokes the **untrusted planner** (ADR-007); it composes meals optimizing the **weighted objective** (C14) plus fulfillment — **within** the per-meal hard constraints. |
| 5 | System | **Validates the raw output in a transient ACL step** (the AI is untrusted): rejects malformed payloads, auto-splits `Required`/`Restricted` conflicts into separate dishes (C6), leaves impossible cells unfilled. Raw payload is **not persisted** (C10). |
| 6 | System | Writes the validated **pending suggestions** to the **session-keyed store** (MP-O7) and re-renders the grid: in-scope empty cells now show **ghost cells** (dish(es) + reasoning snippet + rolled-up fulfillment/cost), visually distinct from confirmed meals. Occupied cells are untouched. |
| 7 | System | Renders the live **advisory insights panel** (C15 / J10) computed over **confirmed plan + pending suggestions**, plus a persistent **Accept all · Discard · N pending** bar. |
| 8 | User | Works the grid cell-by-cell, any order, mixed: **✓ accept** a ghost · **✗ reject** it (cell back to empty) · **↻ regenerate** that cell (or re-tap Auto-fill day/week to re-roll all *still-pending* cells) · **✎ edit** it (J5 meal editor). |
| 9 | System | **Manual touch confirms** — editing a ghost (swap a dish, change servings, hand-pick a recipe) commits it as a `PlannedMeal`, as does accepting it unchanged. Each commit runs the **normal `AssignMeal` validation** (date-in-week, servings ≥ 1, dishes-XOR-note, **warn**-not-block on hard stance, C9) and is recorded with `source: ai`. The pending entry is removed from the store. **Accept all** commits every remaining ghost in one transaction; **Discard** clears the store. |

**Domain events emitted:** per committed cell, `MealPlanned(householdId, weekStart, date, slotId, source: ai, by, at)`. *(No week-grained `MealPlanProposalAccepted` — there is no single accept moment.)*

**Edge cases:**
- No recipes match a cell's hard constraints (e.g. everyone `Vegan`-`Required`, no vegan recipes) → that ghost renders **unfilled** with an explanatory note; generation does not fail wholesale.
- A slot has **no attendees** for the requested days → skipped (nobody's eating it).
- AI returns a nonexistent recipe / malformed payload → rejected by the ACL (step 5); that cell stays empty (untrusted-input discipline).
- Budget target unmeetable → planner proposes the closest plan and flags the overage in the reasoning, never silently violates a hard stance to hit budget.
- Regenerate after accepting/editing some cells → only **still-pending / empty** in-scope cells are re-rolled; confirmed cells are never touched (C12/C13/MP-O3).
- Manual edit of a **blank** cell (no AI) → direct `AssignMeal(source: manual)`, committed immediately; never enters the pending store.
- User **navigates away / session expires** → the pending store TTLs out; confirmed meals persist. (This is the ephemeral semantics, MP-O7 — suggestions don't survive a refresh in v1.)
- The planner proposes **recipes** only in v1; **product** meals (frozen pizza) and **note** meals (takeout) are manual (C16) and, being occupied, are skipped by auto-fill unless the user asks to replace.

---

### J5 — Manually assign a meal

**Trigger:** User taps an empty cell, **"Add meal"** on an already-filled cell (to stack another meal in the slot — e.g. a separate meal for a member on a different diet), or **"edit"** on an existing meal.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Opens the meal editor for that `(date, slot)`. **Add** starts a new meal in the cell's stack; **edit** loads the chosen meal. Shows the slot's effective attendees (J2 default), with an option to **override** attendance for this instance (C5 — e.g. a guest at Saturday dinner, or just the one member this meal is for). |
| 2 | User | Adds dishes — searches **recipes** and/or **products** (frozen pizza, a prepared food) — and sets servings/quantity per dish (seeded from attendee count, C8); **or** instead enters a free-text **Note** ("Takeout", "Out of town") to mark the slot occupied without a meal (C16). |
| 3 | System | Computes the meal's fulfillment + cost live as dishes are added: **recipe** dishes roll up Recipes read models; **product** dishes read stock + price directly (Inventory/Pricing). A note-meal contributes nothing (C16). |
| 4 | System | If a chosen **recipe/product** dish **violates** an effective attendee's hard stance, **warns** (not blocks, C9) — "Contains poultry; Sara has Poultry = Restricted" — and lets the user proceed or pick another. (Note-meals carry no stance.) |
| 5 | User | Saves the meal. |
| 6 | System | Persists the `PlannedMeal` into the week's `MealPlan`. |

**Domain events emitted:** `MealPlanned(householdId, weekStart, date, slotId, by, at)`.

**Edge cases:**
- **Add meal** on a `(date, slot)` that already has a meal → creates a **new** `PlannedMeal` stacked in that cell (it does not overwrite the existing one); **edit** on a specific meal updates only that meal. This is how "one meal for Mike, another for Jane" in the same slot is expressed.
- Removing all dishes from a meal → that `PlannedMeal` is cleared; sibling meals in the cell renumber to stay contiguous (the cell is empty only once its last meal is removed).
- Per-instance attendance override later cleared → meal reverts to the slot's current default attendees.

---

### J6 — Shop for this week

**Trigger:** User taps "Shop for this week" on the week view (§5d).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Across **every `PlannedDish`** in the week, determines what's missing: **recipe** dishes → fresh `FulfillmentResult` at planned servings (Recipes read model); **product** dishes → is the product in stock for the planned quantity (Inventory). Note-meals are skipped (no dishes). |
| 2 | System | Aggregates the **`Missing`** items across all dishes (excluding untracked staples), summing quantities for the same product. A missing **product** dish adds **that product** directly. |
| 3 | System | Calls Shopping `AddItems(product_id, summedQty, unit_id, source="meal_plan", source_ref=mealPlanId)` for each — reusing the P2-4 add-missing seam; Shopping applies its merge rule (DM-18). |
| 4 | System | Confirms "N items added to your shopping list"; button reflects completion for the session. |

**Domain events emitted:** none (Shopping owns its state).

**Edge cases:**
- Everything for the week is in stock → nothing to add; button disabled / "You're stocked for the week."
- The same product is `Missing` across multiple meals → quantities **sum** before the call (one merged line, not N).
- An untracked staple appears in several dishes → never added (always satisfied, C12 of Recipes carried through).
- Servings changed after a prior "Shop for this week" → re-running recomputes against current planned servings; Shopping merge avoids duplicates.

---

### J7 — Navigate / plan another week

**Trigger:** User taps previous / next week (or "plan next week").

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | User | Moves to an adjacent `week_start`. |
| 2 | System | Loads that week's `MealPlan` (past weeks retained, C2) or lazily presents an empty grid for a future week. |
| 3 | User | Plans the future week with J4 (AI) or J5 (manual); reviews a past week read-only-ish (editing a past week is allowed but unusual). |

**Domain events emitted:** none.

**Edge cases:**
- Jumping to a week with no plan → empty grid, same affordances as J1.
- Past-week plans remain viewable as the analytics substrate (C2); cook history (Recipes `cook_event`) is the record of what was *actually* cooked, distinct from what was *planned*.

---

### J8 — Regenerate or swap a single meal

**Trigger:** While reviewing inline suggestions (J4) or on a saved plan, the user wants a different option for one meal. This is just the `PlanningScope.SingleMeal` case of the same one-screen flow (MP-O3/MP-O7).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | User | Taps **↻ regenerate** on one cell, or **swap** to browse alternatives. |
| 2 | System | Re-proposes **only that cell** (MP-O3) against the same constraints + that meal's effective-attendee stances, rewriting that one entry in the pending store; other pending suggestions and the committed plan are untouched. |
| 3 | User | Accepts the new option (commits with `source: ai`), or picks a specific recipe (manual swap → J5 mechanics, `source: manual`). |

**Domain events emitted:** none until confirmed, then `MealPlanned(source: ai|manual)` for that cell (as J4/J5).

**Edge cases:**
- Repeated regeneration exhausts good candidates → planner may repeat or report "no better option"; never violates a hard stance to produce variety.

---

### J9 — Reschedule a meal (drag and drop)

**Trigger:** User drags a `PlannedMeal` from one cell of the week grid to another ("we'll have the
pizza tonight, not tomorrow").

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | User | Drags the meal from its `(date, slot)` cell and drops it on another cell in the same week. |
| 2 | System | The meal **relocates** into the target cell, appended to the bottom of that cell's stack (a cell holds an ordered stack of meals, so the target simply gains a meal — no swap, C11). The source cell's remaining meals renumber to stay contiguous. |
| 3 | System | Each moved meal's **per-instance attendance override travels with it**; a meal with no override now **inherits the destination slot's default attendees** (C5). Dish servings are unchanged. |
| 4 | System | Persists the `MealPlan`; the meal's fulfillment/cost are unaffected by the move (same dishes, same servings) and re-render in their new cells. |

**Domain events emitted:** `MealMoved(householdId, weekStart, fromDate, fromSlotId, toDate, toSlotId, by, at)`.

**Edge cases:**
- Drag a meal onto itself / no movement → no-op.
- Relocate across **different slots** (Tue dinner → Wed lunch) → the meal keeps its own override; a meal without an override picks up its new slot's default attendees (the planner's hard constraints are **not** re-validated on a manual move — C12, the user is authoritative).
- Drop onto an **occupied** cell → the meal joins that cell's stack (it does **not** displace what's there); to thin out a cell, edit or remove a meal directly.
- Cross-**week** drag → out of scope for v1 (the grid shows one week; `MoveMeal` operates within one `MealPlan`). A future enhancement.

---

### J10 — Review plan insights

**Trigger:** Insights render automatically alongside a proposal (J4 step 7) and on any saved week's
plan (J1); the user reads them.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Computes **`PlanInsights`** read-side over the current set of planned/proposed meals + Inventory expiring stock + Recipes reads (no new ports, C15). |
| 2 | System | Surfaces advisory callouts, e.g.: **`UnusedExpiring`** ("Chicken breast expires in 3 days, but isn't used in this plan"); **`OverBudget`** ("Est. $96 vs $80 target"); **`Repetition`** ("Tacos planned twice this week"; "Same as last Tuesday"); **`UnfilledSlot`** ("No dinner planned Thu–Fri"); **`HardConflictResolved`** ("Split into a vegan + a meat dish for Sara & you"). |
| 3 | User | Acts on an insight at will — e.g. taps `UnusedExpiring` to find recipes using that ingredient (links to Recipes browse "Use soon"), or regenerates a meal (J8). Insights **never block** (C12). |

**Domain events emitted:** none (pure read).

**Edge cases:**
- A clean plan → "No issues — this plan uses your expiring stock and fits your budget."
- `UnusedExpiring` for an item no recipe uses → still surfaced (the user may want to plan around it or shop differently); links to nothing actionable, which is itself signal.
- Insights are recomputed on every plan change (add / swap / move / clear), so they always reflect the current plan.

---

## Cross-Cutting Notes

**Minimum-friction principle (carried from Recipes J4).** The planner and the manual editor never *block* on preference violations or shortfalls. The AI **respects** hard stances by construction (and the ACL enforces it on its output), but a human manually assigning a meal can knowingly override (C9). The system records what the household **planned**, not a policy it enforces against them.

**The planner is a server-side untrusted function (ADR-007).** The AI's output is validated in a transient ACL step and held as **pending suggestions** in a session-keyed store — quarantined (never in the schema, never read by a domain query) — before any of it becomes a real `MealPlan`. Unlike Intake's persisted `ImportSession`, this staging is **transient and reviewed inline** (MP-O7): meal suggestions are cheap to regenerate, so nothing durable need survive a session in v1. No model output crosses into the plan unconfirmed, and the household AI key is never sent to the client (DM-7).

**Attendance is the join between slots and preferences.** A meal's constraint set is the union of its **effective attendees'** stances — not the whole household's. This is what lets "lunch is planned for me alone" and "dinner must satisfy both of us" coexist, and it is the mechanism (with multi-dish meals) by which conflicting hard preferences resolve into separate dishes (C5/C6).

**Fulfillment and cost are borrowed, never recomputed.** Meal Planning does not own a fulfillment or costing engine; it **rolls up** the Recipes read models (`FulfillmentResult`, `CostPerServing`) across a meal's dishes and across the week. Those reads are always fresh (Recipes cross-cutting note), so the plan grid reflects live pantry state.

**Deal-awareness is a Phase-5 seam (C7).** SPEC §5b lists "checks active deals" as a generation input and VISION calls the planner deal-aware. With Deals deferred to Phase 5, Phase 3 ships the planner deal-blind; the cost input is purchase-price history only, and adding deal pricing later is an additional `IPriceReader`-style input, not a re-model.

**Suggestions, not decisions (C12).** The AI proposes; the human disposes. Every machine-chosen meal is overridable — at review and after it is saved — by swapping, hand-editing, clearing, or rescheduling it. The planner enforces hard stances on **its own** output (the ACL), but it never locks a cell against the user. A meal once accepted is an ordinary `PlannedMeal`, indistinguishable in editability from one the user assigned by hand.

**Planning is a spectrum, not a mode (C13).** There is no "manual mode" vs "AI mode." A household can hand-pick every meal, generate the whole week, or auto-fill a single day / a slot across days / one meal — and **mix all of these in the same plan**. Auto-fill is additive: it fills empty cells in the chosen scope and leaves existing meals alone unless the user asks to replace them. Drag-and-drop reschedule (J9) works identically regardless of how a meal got there.

**Weights tune; they never override (C14).** The `PlanningWeights` sliders (Cost / Waste / Variety, summing to 100) bias the planner's **soft** objective only. No slider position can cause a meal to violate an effective attendee's hard stance — `Required`/`Restricted` sit outside the optimization entirely (M5). The **budget target** is likewise a soft ceiling, not a hard one: the planner reports an `OverBudget` insight rather than dropping a dietary requirement to hit a number. **Variety** is the lever that finally consumes the retained plan history (C2) and Recipes `cook_event` — "don't plan what we just ate."

**Insights are advisory and always live (C15).** `PlanInsights` are computed read-side over the current plan and recomputed on every change. They observe, link, and nudge — surfacing expiring stock the plan ignores, repetition, over-budget, and unfilled slots — but never block a save or a generation (C12). They are the planner's "here's what I notice," not "here's what you must fix."
