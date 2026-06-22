# Meal Planning — Domain Model

> **Status:** Modeling calls MP-O1–MP-O8 resolved — Phase 3. **Rendered into tables in
> [DataModels/mealplanning.md](../../DataModels/mealplanning.md) (DM-21); build sequenced in
> [PHASE-3-PLAN.md](../../../PHASE-3-PLAN.md).**
>
> **Purpose:** Translate the confirmed [ubiquitous language](mealplanning-ubiquitous-language.md)
> into aggregate boundaries, invariants, behaviours, value objects, and the cross-context ports the
> Meal Planning context needs. This is the contract the Data Schema step renders into the
> `meal_planning` schema and the App Services step implements. Terms here appear **verbatim** in the
> language doc.
>
> **Bounded context:** Meal Planning (`meal_planning` schema, Phase 3). References Recipes,
> Inventory, Pricing, Shopping, Identity **by ID only** — no enforced cross-context FKs (DM-3).
>
> **Code shape:** aggregates follow the established pattern — `AggregateRoot<TId>` with
> strongly-typed IDs, private setters, factory `Create`/`Start`, `IClock`-stamped mutators,
> `Result<T>`/`Error` for failable operations (see `Plantry.Recipes.Domain.Recipe`,
> `Plantry.Inventory.Domain.ProductStock`). The AI ACL borrows Intake's
> `Plantry.Intake.Domain.ImportSession` *intent* (review-then-commit, ADR-007/ADR-010) but **not** its
> persisted-aggregate shape — Meal Planning's staging is a transient session store (§6, MP-O7).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## 1. Context boundary & dependency rules

Meal Planning is a **downstream consumer** of Recipes (and, through Recipes' read models, of
Inventory and Pricing). It owns **three** aggregates — `MealPlan`, `MealSlotConfig`, `UserPreference`
— plus a **transient** pending-suggestion store (§6), and reaches everything else through ports (§8).

| Rule | Statement |
|---|---|
| **Ownership** | Meal Planning owns `MealPlan` (+ `PlannedMeal` / `PlannedDish`), `MealSlotConfig` (+ `MealSlot`), and `UserPreference` (+ `TagStance`). Nothing else. AI suggestions are held **transiently** (§6), not as an owned aggregate. |
| **Reference by ID** | `recipe_id`, `tag_id`, `user_id`, `household_id`, the shopping `AddItems` target — all soft-refs, never object graphs, never FKs across schemas (DM-3). |
| **Reads are always fresh** | `MealFulfillment` and `MealCost` are **never** persisted on `MealPlan`. They roll up the Recipes read models at query time (cross-cutting note, J1). |
| **Planning plans; it does not cook** | Meal Planning never calls `Consume`. Decrementing stock happens when the user **cooks** a planned meal via the existing Recipes Cook flow (ADR-011). The seam is one-directional: planning produces intent, cooking realizes it. |
| **AI is untrusted (ADR-007)** | The planner's raw output is validated in a **transient ACL step** and held in a **quarantined, session-keyed pending store** (§6) — never in a domain aggregate; only user-confirmed, ACL-validated meals cross into `MealPlan`. |
| **The human is authoritative (C12)** | Hard stances bind the planner's **own** proposals, never the user. Once in the `MealPlan`, every meal — generated, auto-filled, or hand-assigned — is equally editable: swap, hand-edit, clear, or reschedule. No code path locks a cell against the user. Planning is a **spectrum** (manual ↔ automatic, any `PlanningScope`), not a mode (C13). |
| **Same-context FKs allowed** | `MealPlan`↔`PlannedMeal`↔`PlannedDish`, `MealSlotConfig`↔`MealSlot`, `UserPreference`↔`TagStance` are all inside `meal_planning`, so they *may* use real FKs (DM-3 permits hard FKs within a context). The pending suggestion store is transient infra (§6), not part of the schema. |

---

## 2. Aggregate map

| Aggregate root | Identity | Owns (composition) | References by ID | Lifecycle |
|---|---|---|---|---|
| **MealPlan** | `MealPlanId` (unique per `(household, WeekStart)`) | ordered `PlannedMeal[]` → each owns ordered `PlannedDish[]` **or** a `Note` | `HouseholdId`; each `PlannedMeal.MealSlotId`; each `PlannedDish.RecipeId` **XOR** `ProductId`; override `UserId[]` | Mutable; **week-keyed, history retained** (C2) |
| **MealSlotConfig** | `MealSlotConfigId` (one per household) | ordered `MealSlot[]` | `HouseholdId`; each `MealSlot.DefaultAttendees` (`UserId[]`) | Mutable; slots **soft-archived**, not deleted (MP-O2) |
| **UserPreference** | `UserPreferenceId` (one per `(household, user)`) | `TagStance[]` | `HouseholdId`, `UserId`; each `TagStance.TagId` | Mutable; edited as a profile (MP-O1) |

> **Three aggregates, not four.** AI suggestions are *not* a fourth aggregate. They are validated,
> typed `ProposedMeal`s held in a **transient, session-keyed pending store** (§6, MP-O7) — quarantined
> from the schema and from every domain read. Only confirmed meals become `PlannedMeal`s.

`PlannedMeal`, `PlannedDish`, `MealSlot`, and `TagStance` are **entities local to their roots** —
addressable while loaded, never referenced from outside the aggregate. See **MP-O1** (profile
granularity) and **MP-O2** (slot identity).

`MealSlotConfig` is its **own root** (one per household), not owned by `MealPlan`, because the slot
vocabulary has an independent lifecycle (configured once, referenced by every week's plan) — exactly
the relationship Recipes' `Tag` has to `Recipe`. `MealPlan`'s `PlannedMeal`s reference a `MealSlot`
**by ID**.

---

## 3. MealPlan aggregate

### 3.1 MealPlan (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `MealPlanId` | |
| `HouseholdId` | `HouseholdId` | tenancy |
| `WeekStart` | `DateOnly` | the ISO-week Monday; **unique per household** (M1) — normalized on create (M8) |
| `Meals` | `IReadOnlyList<PlannedMeal>` | an ordered stack per `(Date, MealSlotId)` — 0..n meals, each with its own dishes/attendees/`Ordinal` (M2) |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | |

**Behaviours**

| Method | Effect |
|---|---|
| `MealPlan.Start(householdId, weekStart, clock)` | Factory. Normalizes `weekStart` to its Monday (M8); starts empty. |
| `AssignMeal(date, slotId, content, attendeesOverride?, reasoning?, source, mealId?, clock)` | **Adds** a `PlannedMeal` to the `(date, slotId)` cell when `mealId` is null (allocating the next `Ordinal` in that cell's stack), or **updates** the identified meal when `mealId` is set (M2). `content` is **either** dishes (each a recipe XOR product) **or** a `Note` (M12/M13). `source` is `manual` (J5) or `ai` (accepting a suggestion, J4 — `reasoning` carried through). Validates the date falls in `[WeekStart, WeekStart+6]`, dish servings ≥ 1 (M3), and the dishes-XOR-note rule. Emits **MealPlanned**(`source`). |
| `ClearMeal(mealId, clock)` | Removes the identified `PlannedMeal`; renumbers the remaining meals in that cell to stay contiguous (M2). |
| `MoveMeal(mealId, toDate, toSlotId, clock)` | Reschedules within the week (C11 / J9): **relocates** the meal into the target cell's stack (appended at the next `Ordinal`) and renumbers the source cell — **no swap** (a cell is a stack, so the destination simply gains a meal). The per-instance `AttendeesOverride` **travels with** the meal; an unset override inherits the destination slot's default (M4). Dishes/servings unchanged. Hard constraints are **not** re-validated (C12 — manual moves are authoritative). Emits **MealMoved**. Preserves M2. |
| `SetMealAttendees(mealId, attendeesOverride?, clock)` | Sets/clears the per-instance override; `null` reverts to slot default (M4). |
| `ApplyProposal(acceptedMeals, clock)` | **Accept-all** convenience: bulk-adds the supplied validated suggestions (from the pending store) as `PlannedMeal`s in one transaction (J4), each via the same path/validation as `AssignMeal`. **Skips any cell that already holds a meal** — the AI never appends a second meal to an occupied cell. Single-cell accept and accept-with-edit go through `AssignMeal` directly. Emits **MealPlanned**(`source: ai`) per meal. |

> `MealPlan` knows nothing of Recipes/Inventory/Pricing. Fulfillment, cost, hard-constraint
> validation, and "shop for the week" are **services** (§7) over ports (§8), not root methods.

### 3.2 PlannedMeal (entity, child of MealPlan)

| Field | Type | Notes |
|---|---|---|
| `Id` | `PlannedMealId` | local to the aggregate (MP-O1) |
| `Date` | `DateOnly` | within the plan's week (M2) |
| `MealSlotId` | `MealSlotId` | soft-ref to a `MealSlot` (the slot may be archived but still resolvable, MP-O2) |
| `Ordinal` | `int` | position within the `(Date, MealSlotId)` cell's stack, 1..n contiguous (M2); lets one member's meal sit alongside another's in the same slot |
| `AttendeesOverride` | `IReadOnlyList<UserId>?` | null ⇒ inherit the slot's default attendees (C5 / M4) |
| `Reasoning` | `string?` | the AI snippet when this meal came from a proposal; null when hand-assigned |
| `Dishes` | `IReadOnlyList<PlannedDish>` | 0..n; ordered — **XOR** `Note` (M13) |
| `Note` | `string?` | free-text occupied-slot marker ("Takeout", "Out of town"); set ⇔ `Dishes` empty (C16 / M13). Manual-only |

A persisted `PlannedMeal` is **occupied** by exactly one form: a non-empty `Dishes` set **or** a
`Note` (M13). Both-empty isn't persisted (it's just an empty cell). Note-meals carry no attendee
constraints and contribute nothing to fulfillment/cost/shopping.

### 3.3 PlannedDish (entity, child of PlannedMeal)

| Field | Type | Notes |
|---|---|---|
| `Id` | `PlannedDishId` | local |
| `RecipeId` | `RecipeId?` | soft-ref (Recipes, DM-20). **XOR** `ProductId` (M12) |
| `ProductId` | `ProductId?` | soft-ref (Catalog, DM-10) — prepared food / future recipe-output leftover (C16). **XOR** `RecipeId` |
| `Servings` | `int` | ≥ 1 (M3); for a product dish this is the quantity (in the product's default unit); seeded from effective attendee count (C8), user-overridable |
| `Ordinal` | `int` | position within the meal (main, side, …) |

`PlannedDish` is a leaf with no behaviour; it is constructed validated and replaced via its parent.
Exactly one of `RecipeId` / `ProductId` is set (M12): a **recipe** dish is cooked (Recipes Cook flow)
and fulfillment/cost come from Recipes' read models; a **product** dish resolves fulfillment from
stock and cost from price directly (Inventory/Pricing). The product-meal **eat→consume** action is
deferred (FUTURE.md, recipe-output products).

---

## 4. MealSlotConfig aggregate

The household's ordered meal-slot vocabulary (§7h). One per household.

### 4.1 MealSlotConfig (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `MealSlotConfigId` | |
| `HouseholdId` | `HouseholdId` | one per household |
| `Slots` | `IReadOnlyList<MealSlot>` | ordered by `Ordinal`; active + archived |

**Behaviours:** `AddSlot(label, defaultAttendees, clock)`, `RenameSlot(slotId, label, clock)`,
`ReorderSlots(orderedIds, clock)`, `SetDefaultAttendees(slotId, userIds, clock)`,
`ArchiveSlot(slotId, clock)` (soft — MP-O2). Validates labels non-blank + unique per household (M9),
ordinals contiguous among active slots. Seeded with default slots at household creation (DM-9
pattern) — Breakfast / Lunch / Dinner.

### 4.2 MealSlot (entity, child of MealSlotConfig)

| Field | Type | Notes |
|---|---|---|
| `Id` | `MealSlotId` | stable across renames/reorders so `PlannedMeal`s never orphan (MP-O2) |
| `Label` | `string` | free text (C4); unique-per-household among active slots (M9) |
| `Ordinal` | `int` | order within a day |
| `DefaultAttendees` | `IReadOnlyList<UserId>` | members who normally eat this slot (C5) |
| `ArchivedAt` | `DateTimeOffset?` | soft-archive; archived slots leave the future grid but stay resolvable for history (MP-O2) |

---

## 5. UserPreference aggregate

A member's dietary profile (C1). One per `(household, user)`; the per-member counterpart that the
planner reads. **MP-O1**: modelled as a **per-user profile root** owning `TagStance` children — it is
edited as a unit (the preferences screen, J3) and is small and bounded.

### 5.1 UserPreference (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `UserPreferenceId` | |
| `HouseholdId` | `HouseholdId` | tenancy |
| `UserId` | `UserId` | the member; **unique per `(household, user)`** (M6) |
| `Stances` | `IReadOnlyList<TagStance>` | one per `tag_id` (M6); Neutral = absent |

**Behaviours:** `SetStance(tagId, Stance, clock)` (upsert; setting `Neutral` **removes** the entry),
`ClearStance(tagId, clock)`. `UserPreference.Create(householdId, userId, clock)` lazily on first edit.

### 5.2 TagStance (entity, child of UserPreference)

| Field | Type | Notes |
|---|---|---|
| `Id` | `TagStanceId` | local |
| `TagId` | `TagId` | soft-ref to the Recipes tag vocabulary (DM-20) |
| `Stance` | `Stance` enum | `Required` / `Preferred` / `Disliked` / `Restricted` (never `Neutral` — that's absence, M6) |

---

## 6. The AI ACL — a transient pending store, not an aggregate (MP-O7)

The planner's output is quarantined by an **anticorruption layer**, per ADR-007 (C10). Unlike Intake's
persisted `ImportSession`, Meal Planning's ACL is **not a domain aggregate**: the raw model payload is
validated **in memory inside `GeneratePlan`** and the resulting typed, validated `ProposedMeal`s are
written to a **transient, session-keyed pending store** — never to the `meal_planning` schema and
never to any domain read. Only user-confirmed suggestions cross into `MealPlan` (via `AssignMeal` /
`ApplyProposal`). See **MP-O7** for the why (and the deliberate divergence from Intake).

### 6.1 The pending-suggestion store (transient infrastructure)

| Aspect | Choice |
|---|---|
| **Home** | Server-side session store, keyed `(household, weekStart, session)` — chosen mechanism (in-proc session vs `IDistributedCache`) is a data/infra-pass detail; not in the schema. |
| **Lifetime** | **Ephemeral**: TTL'd; cleared on accept-all / discard / navigate-away. A member's suggestions are **private to their session** until committed. |
| **Contents** | The request `PlanningConstraints` + the validated `ProposedMeal`s. The **raw payload is not persisted** (validated in memory; loggable to telemetry only). |
| **Quarantine** | No domain read (`ShopForWeek`, fulfillment/cost roll-ups, the Variety lever's plan history, past-week display) ever touches it. `MealPlan` stays committed-reality by construction. |

There is **no `generating → ready → accepted/discarded` aggregate lifecycle** and no persisted status —
the store simply exists while a session has live suggestions and is gone otherwise. Regeneration
(`PlanningScope.SingleMeal` / `Day` / `Week`) rewrites the still-pending entries in scope; confirmed
meals are never touched (C12/C13).

### 6.2 ProposedMeal (the validated suggestion shape)

`{ Date, MealSlotId, EffectiveAttendees (snapshot), ProposedDishes[] (recipeId, servings, ordinal),
Reasoning }`. The pre-confirmation twin of `PlannedMeal`; only typed, validated fields are carried —
never the raw payload. It is a **transient DTO** held in the pending store (§6.1), not a persisted
entity. On accept it is mapped to a `PlannedMeal` via `AssignMeal` (`source: ai`).

---

## 7. Domain & application services

None lives *on* an aggregate — keeping the roots pure of Recipes/Inventory/Pricing knowledge.

| Service | Responsibility | Touches |
|---|---|---|
| **MealConstraintResolver** (domain) | `Resolve(plannedMeal, slotConfig, preferences[]) → MealConstraints`. Resolves the **effective AttendeeSet** (override ?? slot default, C5), then unions hard stances and averages soft stances across those attendees (MP-O4). The pure heart of the planner. | `UserPreference`, `MealSlotConfig` (in-context) |
| **PlanFulfillmentService** (domain) | `RollUp(plannedMeal | week) → MealFulfillment`. **Recipe** dishes aggregate the Recipes `FulfillmentResult` at planned servings; **product** dishes resolve "in stock for the planned quantity?" via `IInventoryStockReader`; note-meals contribute none. | `IRecipeReadModel`, `IInventoryStockReader` |
| **PlanCostingService** (domain) | `RollUp(plannedMeal | week) → MealCost`. **Recipe** dishes sum `CostPerServing × servings` (Recipes); **product** dishes use price × quantity via `IPriceReader`; propagates `CostCompleteness`. Deal-blind in P3 (C7). | `IRecipeReadModel`, `IPriceReader` |
| **PlanInsightsService** (domain) | `Inspect(plan | proposal) → PlanInsights` (C15 / J10). Read-side, recomputed on every change: `UnusedExpiring` (expiring stock − products any planned dish consumes), `OverBudget` (`MealCost` week sum vs budget target), `Repetition` (recipe repeated this week or vs retained recent plans / `cook_event`), `UnfilledSlot`. **No new ports.** Note: `HardConflictResolved` removed (so5.4) — hard-stance conflicts (C6) are an unfillable-cell state rendered in-cell, not a saved-plan rail insight. | `IInventoryStockReader`, `IRecipeReadModel`, in-context plan history |
| **HardConflictDetector** (domain) | `Detect(constraints, candidates) → HardStanceConflict?`. Pure, stateless. Determines whether no single candidate recipe can satisfy all attendees' hard stances simultaneously (C6). Returns null when reconcilable; returns a `HardStanceConflict` (attendee IDs + clashing Required tag IDs) when irreconcilable. Called before dispatching to `IMealPlanner`; conflicted cells are skipped from the AI request, counted as unfilled, and flagged in-cell. No I/O. | in-context |
| **UnfulfillabilityDetector** (domain) | `DetectAsync(constraints, anyRecipeWithTag) → UnfulfillableResult?`. Pure, stateless. Determines whether an attendee's Required tag has **zero** matching recipes in the household's **full recipe corpus** (not the 50-cap candidate list). Called after `HardConflictDetector` for each cell; a cell already classified as HardConflict is not re-checked here (HardConflict takes precedence — the fix is "find a compromise", not "add a recipe"). Returns `(AttendeeId, UnfulfillableTagId)` on first gap found, null when every Required tag has ≥1 recipe. Uses `IRecipeReadModel.AnyRecipeWithTagAsync` (targeted full-corpus `AnyAsync`, bypasses the 50-cap search). No I/O beyond the injected delegate. | `IRecipeReadModel.AnyRecipeWithTagAsync` (via delegate) |
| **GeneratePlan** (application) | J4. Takes a **`PlanningScope`** (week / day / slot-series / single meal, C13) + `PlanningConstraints` (incl. **`PlanningWeights`** Cost/Waste/Variety and the optional budget target, C14); targets only **empty** cells in scope unless `replace = true`. For each cell: (1) runs `HardConflictDetector` — irreconcilable cells (C6) are left unfilled and flagged in-cell; (2) runs `UnfulfillabilityDetector` — corpus-gap cells (so5.5) are also left unfilled and flagged; (3) for satisfiable cells: gathers context, passes to `IMealPlanner` (untrusted, ADR-007), validates via `ProposalAcl`, and writes to the **session-keyed pending store** (§6). Returns `GeneratePlanResult(ProposedCount, UnfilledCount, Conflicts: IReadOnlyList<HardConflictCell>, UnfulfillableCells: IReadOnlyList<UnfulfillableCell>)` for rendering. Weights tune the soft objective only; they never relax a hard stance (M5/M11). No per-attendee auto-split in P3 (deferred to FUTURE). | `IRecipeReadModel`, `ITagReader`, `IInventoryStockReader`, `IMealPlanner`, in-context resolver |
| **RegenerateMeal** (application) | J8. The `PlanningScope.SingleMeal` convenience over `GeneratePlan` (MP-O3): re-proposes one meal against the same constraints and rewrites that one entry in the pending store; leaves other pending suggestions and the committed plan untouched. | as above |
| **MoveMeal** (application) | J9. Reschedules a `PlannedMeal` within the week via `MealPlan.MoveMeal` (relocate into the target cell's stack, C11); emits **MealMoved**. No re-validation (C12). | in-context |
| **AcceptProposal** (application) | J4. Writes confirmed suggestions from the pending store into the week's `MealPlan` — **accept-all** via `ApplyProposal`, **per-cell** via `AssignMeal` (`source: ai`) — re-validating each (the accept POST is the trust boundary), and clears the accepted entries from the store. **Reject** simply drops an entry; **discard** clears the store. | in-context |
| **AssignMeal** (application) | J5. Upserts a `PlannedMeal` with chosen dishes (recipe **or** product) **or** a `Note` (C16) + optional attendance override; **warns (does not block)** on a hard-stance violation (C9). Validates a product dish references a real Catalog product. | `IRecipeReadModel`, `ICatalogProductReader`, in-context resolver |
| **ShopForWeek** (application) | J6. Across all `PlannedDish`es: **recipe** dishes contribute their `Missing` ingredients at planned servings; **product** dishes contribute the **product itself** if short on stock; note-meals are skipped. Sums per product (excluding untracked) and calls Shopping `AddItems(..., source="meal_plan", source_ref=mealPlanId)`. | `IRecipeReadModel`, `IInventoryStockReader`, `IShoppingListWriter` |
| **ManageSlots** (application) | J2. `MealSlotConfig` CRUD + default attendees. | `IHouseholdMemberReader` |
| **SetPreferences** (application) | J3. `UserPreference` edits; lists tags via the Recipes vocabulary. | `ITagReader`, `IHouseholdMemberReader` |

**Accept atomicity (note for the schema/app step):** **accept-all** (`ApplyProposal`) applies its
meals in one transaction so a half-applied plan can't result. Generation is *not* transactional with
the plan — suggestions live only in the **transient session-keyed pending store** (§6), which is TTL'd
and cleared on accept/discard/navigate-away. This **diverges from Intake's durable `ImportSession`**
(MP-O7): meal suggestions are cheap to regenerate and reviewed in one sitting, so there is nothing
durable to survive between sessions in v1.

---

## 8. Cross-context ports (anticorruption layer)

Meal Planning depends on these interfaces; the owning contexts implement them. All traffic is by ID
(DM-3).

| Port | Used by | Surface (read = R / write = W) |
|---|---|---|
| **IRecipeReadModel** | Generate, RollUp, Assign, ShopForWeek, Insights | R: list candidate recipes with `tag_id[]`, default servings, **FulfillmentResult** + **CostPerServing** at a given servings; **recent `cook_event` history per recipe** (for the Variety lever, C14) (Recipes read models, DM-20) |
| **ITagReader** | SetPreferences | R: resolve `tag_id` → name + cosmetic category for the preference UI (Recipes vocabulary, DM-20) |
| **ICatalogProductReader** | Assign, RollUp | R: product name, `track_stock`, default unit, tags — to validate and render a **product dish** (Catalog, DM-10) |
| **IInventoryStockReader** | Generate, RollUp, ShopForWeek | R: expiring stock + available quantity per product (DM-13) — the **same contract Recipes already defines**, reused; also resolves **product-dish** fulfillment |
| **IPriceReader** | PlanCosting (product dishes) | R: representative price per product (DM-17), for **product-dish** cost; recipe-dish cost still flows via `IRecipeReadModel`. Phase-3 deal-blind (C7) |
| **IShoppingListWriter** | ShopForWeek | W: `AddItems` with provenance (DM-18) — the **P2-4 seam**, reused |
| **IHouseholdMemberReader** | ManageSlots, SetPreferences | R: list current household members (Identity, DM-6) for attendance + preference owners |
| **IMealPlanner** | Generate, RegenerateMeal | W/R: the **untrusted planner function** (ADR-007). In: gathered context + `MealConstraints`. Out: raw suggestion payload (validated in the `GeneratePlan` ACL step, never persisted — §6). Implemented in `Plantry.MealPlanning.Infrastructure` over the household AI key (DM-7), exactly as Intake wraps its `ChatClient`. |

> **`IRecipeReadModel` over re-implementing fulfillment.** Meal Planning deliberately does **not**
> own a fulfillment/costing engine. Recipes already computes `FulfillmentResult` / `CostPerServing`
> fresh from Inventory + Pricing (DM-20 §6); Meal Planning consumes them and only **rolls up** across
> a meal's dishes and the week. This keeps the single source of truth in Recipes and avoids two
> divergent fulfillment computations.

---

## 9. Domain events

| Event | Payload | Emitted by |
|---|---|---|
| **MealPlanned** | `householdId, weekStart, date, slotId, source` (`manual` \| `ai`)`, by, at` | `MealPlan.AssignMeal` — manual assign (J5) **and** accepting an AI suggestion (J4); `source` distinguishes them |
| **MealMoved** | `householdId, weekStart, fromDate, fromSlotId, toDate, toSlotId, by, at` | `MealPlan.MoveMeal` (J9); one per move (relocate into the target stack, no swap) |

No Phase-3 cross-context reaction subscribes to these (Shopping is called directly via a port); they
exist for attribution/audit and a future analytics consumer — kept light, as Recipes did.

---

## 10. Invariants (consolidated)

| # | Invariant | Source | Enforced |
|---|---|---|---|
| **M1** | `MealPlan` unique per `(household_id, WeekStart)` | C2 | App check + DB `UNIQUE` |
| **M2** | A `(MealPlan, Date, MealSlotId)` cell holds an **ordered stack** of 0..n `PlannedMeal`s; `Ordinal`s contiguous `1..n` within the cell; `Date` ∈ `[WeekStart, WeekStart+6]`. Preserved under `MoveMeal` (relocate into the target stack, C11). | J5, J9 | `AssignMeal`, `MoveMeal`, DB `UNIQUE(meal_plan_id, date, meal_slot_id, ordinal)` |
| **M3** | `PlannedDish.Servings ≥ 1`; dish `Ordinal`s contiguous within a meal | C8 | Aggregate |
| **M4** | Effective attendees = `PlannedMeal.AttendeesOverride ?? MealSlot.DefaultAttendees`, intersected with current household members | C5 | Resolver + read-time membership filter |
| **M5** | An **AI-proposed** meal satisfies every effective attendee's hard stances: no dish carries a `Restricted` tag; every attendee's `Required` tags are all met by ≥ 1 surviving dish. If no single candidate satisfies all attendees simultaneously (C6 — **HardConflict**), the cell is left **unfilled and flagged in-cell**. If an attendee's Required tag has **zero** recipes in the full corpus (**Unfulfillable**, so5.5), the cell is also left unfilled and flagged — with a distinct in-cell UI prompting the user to add a recipe for that dietary need. **HardConflict** takes precedence over **Unfulfillable** when both apply. No per-attendee auto-split in P3 (deferred to FUTURE). **Manual** assignment **warns, never blocks** (C9). | C6, C9 | `HardConflictDetector` + `UnfulfillabilityDetector` + `ProposalAcl` (hard) / `AssignMeal` (warn) |
| **M6** | One `TagStance` per `(UserPreference, tag_id)`; `Stance = Neutral` ⇒ no row | C1 | Aggregate (`SetStance` removes on Neutral) |
| **M7** | Only user-confirmed, ACL-validated suggestions cross into `MealPlan`; the raw payload is **never persisted** and the pending store is **never read by any domain query** | C10, MP-O7 | Transient ACL in `GeneratePlan` + session-keyed pending store (§6) + re-validation on accept |
| **M8** | `WeekStart` is normalized to the ISO-week Monday | C2 | `MealPlan.Start` |
| **M9** | `MealSlot.Label` non-blank + unique per household among active slots; active ordinals contiguous | C4 | `MealSlotConfig` |
| **M10** | A `MealSlot` is **soft-archived**, never hard-deleted, while any `PlannedMeal` references it | MP-O2 | `ArchiveSlot` |
| **M11** | `PlanningWeights` are **normalized** (Cost + Waste + Variety [+ Deals] = 100); they bias the **soft** objective only and **cannot** relax a hard stance (M5) or the AI ACL. The budget target is a **soft** ceiling — exceeding it yields an `OverBudget` insight, never a dropped hard stance | C14 | `GeneratePlan` / `MealConstraintResolver` (weights applied after hard filter) |
| **M12** | A `PlannedDish` has **exactly one** of `RecipeId` / `ProductId` set | C16 | Aggregate + DB `CHECK (num_nonnulls(recipe_id, product_id) = 1)` |
| **M13** | A persisted `PlannedMeal` is **dishes XOR `Note`**: a non-empty dish set **or** a non-null `Note`, never both | C16 | Aggregate + DB `CHECK` |

---

## 11. Resolved modeling calls

- **MP-O1 — `UserPreference` granularity ✅.** Modelled as a **per-`(household, user)` profile root**
  owning `TagStance` children, **not** a flat per-edge table. The preferences screen (J3) edits a
  member's whole stance set as a unit, so the profile *is* the natural consistency boundary and
  aggregate. It stays small (bounded by the household tag count). Reads by the planner are by
  `(household, user)`. **Upgrade trigger:** if stances ever need independent lifecycles (e.g.
  time-boxed "no dairy this month"), promote `TagStance` to its own root then — a contained change.

- **MP-O2 — `MealSlot` identity across config edits ✅.** Each `MealSlot` has a **stable
  `MealSlotId`**; `PlannedMeal`s reference it by ID, so renaming or reordering a slot never orphans
  or rewrites history. Deleting a slot that has any planned meal is a **soft-archive**
  (`ArchivedAt`) — it leaves the future grid but historical `PlannedMeal`s remain resolvable (M10).
  This mirrors Recipes' soft-delete keeping `cook_event` FK-valid (DM-20).

- **MP-O3 — Planning scope ✅.** Generation is parameterized by a **`PlanningScope`** (C13):
  `Week` / `Day(date)` / `SlotSeries(slotId, dates)` / `SingleMeal(date, slotId)`. `GeneratePlan`
  fills only **empty** cells in scope unless `replace = true`; `RegenerateMeal` (J8) is just the
  `SingleMeal` convenience. Review happens **inline on the plan grid** (MP-O7) — there is no separate
  proposal screen; suggestions render as ghost cells accepted/rejected/edited at the meal grain, so
  whole-week accept and per-meal swap/regenerate are the same mechanism at different scopes — and
  manual (J5), auto-fill, and AI-generated meals coexist in one plan. Fully-manual planning is the
  empty-scope endpoint of the same spectrum, never a separate mode.

- **MP-O4 — Where attendee aggregation lives ✅.** In a **domain service**
  (`MealConstraintResolver`), not buried in the AI prompt. The resolver computes the effective
  AttendeeSet and the unioned-hard / averaged-soft `MealConstraints` deterministically; the
  `IMealPlanner` is *given* those constraints and its output is *validated* against them (M5). This
  keeps the hard-preference guarantee in trusted code (ADR-007: the AI is untrusted), with the AI
  doing composition/selection, not safety enforcement.

- **MP-O5 — Weights, budget, and the hard/soft boundary ✅.** The optimization has a strict
  two-tier shape: **hard** (effective-attendee `Required`/`Restricted` + AI ACL) is a *filter* that
  runs first and is non-negotiable; **soft** (`PlanningWeights` Cost/Waste/Variety + the budget
  target) is a *score* over the survivors. The sliders and the budget target therefore live entirely
  in the soft tier — no weight setting can change which recipes are *eligible*, only which eligible
  recipe is *chosen* (M11). This is why a budget overage produces an `OverBudget` **insight** rather
  than dropping a dietary requirement. **`Deals` is a defined lever pinned at 0** in Phase 3 (C7) so
  the slider UI and weight vector need no reshape when Phase-5 deal pricing arrives — it becomes a
  fourth non-zero term and an additional soft input. `PlanInsights` are **read-side and advisory**
  (C15): computed from the same reads, never gating a save or generation (C12).

- **MP-O6 — Slot content: recipe / product / note ✅.** A meal slot is filled by a **two-level XOR**:
  a `PlannedMeal` is **dishes XOR a `Note`** (M13), and a `PlannedDish` is a **recipe XOR a product**
  (M12). This deliberately reverses the earlier "recipe-only" reading (C3 / the original Q3 call)
  because a **product is not free-text** — it carries stock and price, so a product dish is fully
  fulfillment/cost/shopping-computable and adds no special-casing to the math (the thing the
  recipe-only call was protecting). A `Note` is the only free-text form and sits *outside* the dish
  computations by construction. **Scope discipline:** (a) the AI planner proposes **recipes** only in
  v1 — products and notes are manual (proposing in-stock products is a forward extension of the Waste
  lever); (b) the **eat→consume** action for a product meal is **deferred** — recipe meals consume via
  the existing Recipes Cook flow, product meals' consume rides with the **recipe-output product**
  feature (FUTURE.md). This keeps Phase 3 bounded while making the model **forward-compatible**: when
  recipes can output products, planning a leftover is already just a product dish — **zero Meal
  Planning change**.

- **MP-O7 — AI staging: transient store, not an aggregate ✅.** Review happens **inline on the one
  plan grid** — no separate staging screen. The consequence for the model: AI suggestions are held in
  a **transient, session-keyed pending store** (§6), **not** a persisted `MealPlanProposal` aggregate.
  This is a deliberate **divergence from Intake's `ImportSession`** (ADR-007/ADR-010): receipt parsing
  is expensive and reviewed over time, so it must persist staging; meal suggestions are cheap to
  regenerate and reviewed in one sitting, so a transient ACL fits. Quarantine (ADR-007) is **about the
  domain boundary, not the disk** — keeping suggestions out of `MealPlan` and out of every domain read
  satisfies it, and "never persisted to the schema" satisfies it even more strongly. **htmx note:**
  because htmx is stateless per request and the generate→accept flow spans many requests, the pending
  suggestions are held **server-side** (session-keyed) so ghost cells render as ordinary Razor
  fragments — the same pressure that led `Intake/Review` to server-side staging. **Upgrade trigger:**
  if users want suggestions to survive a refresh, or shared in-progress review across household
  members, promote the store to a durable, household-keyed proposal aggregate — a TTL-and-table change,
  not a re-model. Events follow suit: there is **no week-grained `MealPlanProposalAccepted`**; accept
  emits per-cell **`MealPlanned(source: ai)`**. See the [J4 inline generate/review flow](mealplanning-journeys.md#j4--generate-a-meal-plan-with-ai-the-hero--one-screen-inline).

- **MP-O8 — A cell holds a *stack* of meals, not one ✅ (reverses the original M2).** A `(Date,
  MealSlotId)` cell holds an **ordered stack of 0..n `PlannedMeal`s**, each with its own dishes,
  attendees, fulfillment and cost, ordered by a new `PlannedMeal.Ordinal`. The DB key moves from
  `UNIQUE (meal_plan_id, date, meal_slot_id)` to `UNIQUE (meal_plan_id, date, meal_slot_id, ordinal)`.
  **Why the reversal:** the original model collapsed "two things at one slot" onto **dishes within one
  meal** (C3/C6), but dishes share a single meal-level attendee set — so it cannot express *separate
  meals for separate people in the same slot* (feed Mike one meal and Jane another, each with its own
  attendees and its own fulfillment/cost rollup). The prototype's `CellStack` had always shown this
  two-level shape; the schema is now realigned to it. **Consequences:** `AssignMeal` gains an optional
  `mealId` (null = add at the next ordinal, set = update that meal) replacing the upsert-by-cell — this
  also closes the silent-overwrite data-loss path the "Add meal" button had on a filled cell; `ClearMeal`
  is by `mealId` and renumbers siblings; `MoveMeal` **relocates into the target cell's stack — no swap**
  (a stack just gains a meal), which retires the raw-SQL swap path and the deferrable-constraint
  workaround; AI `GeneratePlan`/`ApplyProposal` are unchanged in spirit — they still fill only cells
  with **zero** meals and never append a second. (Filed as **plantry-5eh**, resolving the spec-vs-schema
  tension the Opus critic flagged in plantry-7oy.)

---

## 12. Reconciliation notes

- **ADR-010 "Slot" model — amended.** ADR-010 §Aggregates: "`MealPlan` root (week + `Slot` children,
  each referencing a `recipe_id` and a meal-slot label). `MealSlotConfig` … free-text meal slots …
  that the plan's slots reference. `MealPlanProposal` … writes into `MealPlan` only on accept." This
  model **refines** it: (a) the recurring definition is `MealSlot` (in `MealSlotConfig`), the
  instance is `PlannedMeal`, referencing the slot **by ID** not by label; (b) a meal holds
  **multiple `PlannedDish`es** (C3), not one `recipe_id`; (c) `MealSlot` carries **default
  attendees** and `PlannedMeal` an optional **override** (C5) — new, not in ADR-010; (d)
  `UserPreference`/`Stance` is **owned here** (C1), graduating from FUTURE.md; (e) meals are
  **reschedulable** (`MoveMeal`, relocate into the target cell's stack, C11) and generation is **scoped** (`PlanningScope`,
  C13), with the planner's output **advisory** (C12) — QOL behaviours not contemplated in ADR-010;
  (f) generation is driven by **weighted levers** (`PlanningWeights`, C14) and a plan carries a
  computed **insights** read model (`PlanInsights`, C15) — neither in ADR-010; (g) a `PlannedDish` is
  a **recipe XOR product** and a `PlannedMeal` is **dishes XOR a free-text `Note`** (C16/MP-O6) —
  extending ADR-010's recipe-only slot and reversing the original Q3 "recipe-only" call; (h) the
  **review-then-commit** intent is preserved but its **mechanism changes** (MP-O7): ADR-010's
  `MealPlanProposal` staging *aggregate* becomes a **transient, session-keyed pending store** reviewed
  **inline** on the plan grid (no separate screen), and `MealPlanProposalAccepted` is replaced by
  per-cell `MealPlanned(source: ai)` — a deliberate divergence from Intake's persisted `ImportSession`.
  **Record as an ADR-010 amendment when the schema lands.**

- **FUTURE.md "Tag-driven meal planning & member preferences" — graduates to Phase 3.** Its Stance
  scale and User↔Tag edge are realized as `UserPreference`/`TagStance` (§5). Its "planner aggregation
  across a household sharing a meal" rule is realized — and **sharpened** — as
  `MealConstraintResolver` over the meal's **effective attendees** (not the whole household), with
  hard conflicts detected per-cell and flagged in-cell (C6 detect-and-flag, not auto-split in P3;
  generative per-attendee split with serving apportionment deferred to FUTURE). The Tag stays
  kind-less in Recipes, so this needs **no tag migration**.

---

## Feeds the next step

The **Data Schema** pass renders this into the `meal_planning` schema (provisional **DM-21**):
`meal_plan` (root, `UNIQUE(household_id, week_start)`) + `planned_meal` (child, `UNIQUE(meal_plan_id,
date, meal_slot_id)`, nullable `note` + a dishes-XOR-note `CHECK`) + `planned_dish` (child, nullable
`recipe_id`/`product_id` + `CHECK (num_nonnulls(recipe_id, product_id) = 1)`); `meal_slot_config` + `meal_slot` (`archived_at`
soft-archive, `default_attendees`); `user_preference` + `tag_stance` (`UNIQUE(user_preference_id,
tag_id)`); `meal_plan_proposal` (+ `raw_payload` jsonb ACL, `constraints` jsonb) + `proposed_meal` —
UUIDv7 PKs, composite `(household_id, id)` child FKs, `household_id` + per-household RLS (ADR-008),
`text`+`CHECK` enums (`stance`, proposal `status`), `numeric`/`int` servings, `date` for `week_start`
— per `DataModels/conventions.md`. `MealFulfillment`, `MealCost`, and `PlanInsights` get **no tables**
(computed read-side, rolled up from Recipes + Inventory). `PlanningWeights` + the budget target ride
in the proposal's `constraints` jsonb (transient generation inputs), not their own table. Attendee
sets render as either a child join table or a `uuid[]` column — a schema-pass call. The ports in §8 become the application-service interfaces
wired in the App Services step; `IMealPlanner` is implemented in `Plantry.MealPlanning.Infrastructure`
over the household AI key (DM-7), exactly as Intake wraps its `ChatClient` (ADR-007).
