# Meal Planning (`meal_planning` schema) — Phase 3 ✅

> Renders [`Domains/MealPlanning/mealplanning-domain-model.md`](../Domains/MealPlanning/mealplanning-domain-model.md) into tables. Authority for *rationale* is the domain model + ADRs; this file holds the *shape*. Meal Planning is a **downstream consumer** of Recipes (and, through Recipes' read models, Inventory and Pricing), plus Catalog, Shopping, and Identity — it references all of them **by ID only** (DM-3), with hard FKs only **within** the `meal_planning` schema.

Three aggregates: **`MealPlan`** (root) with `PlannedMeal` children, each owning `PlannedDish` children **or** a free-text `Note`; **`MealSlotConfig`** (root) with `MealSlot` children — the household's ordered meal-slot vocabulary; **`UserPreference`** (root) with `TagStance` children — a member's dietary profile. AI suggestions are **not** a fourth aggregate and get **no table**: they are validated in memory and held in a **transient, session-keyed pending store** (domain model §6, MP-O7), quarantined from the schema and from every domain read. `MealFulfillment`, `MealCost`, `MealConstraints`, and `PlanInsights` are **computed read-side — never tables** (domain model §7).

---

**`meal_plan`** — aggregate root; one week's plan for a household (C2)

| Column | Type | Notes |
|---|---|---|
| `meal_plan_id` | `uuid` PK | UUIDv7; + `UNIQUE (household_id, meal_plan_id)` for child composite FKs |
| `household_id` | `uuid` | tenancy (RLS) |
| `week_start` | `date` | the ISO-week Monday; `UNIQUE (household_id, week_start)` (M1). **Normalized to Monday app-side** in `MealPlan.Start` (M8) — no DB normalization |
| `created_at` / `updated_at` | `timestamptz` | |

The `UNIQUE (household_id, week_start)` index also serves the primary read — "load this household's week" (J1/J7). Past weeks are **retained** as the analytics substrate (C2); there is no archive/delete behaviour on `meal_plan`.

---

**`planned_meal`** — child of `MealPlan`; one meal within a `(date, slot)` cell; **a cell holds an ordered stack of 0..n meals** (C16, M2)

| Column | Type | Notes |
|---|---|---|
| `planned_meal_id` | `uuid` PK | local to the aggregate |
| `household_id`, `meal_plan_id` | `uuid` | composite **FK → `meal_plan (household_id, meal_plan_id)`**, `ON DELETE CASCADE` (within-context, enforced) |
| `date` | `date` NOT NULL | the cell's day; must fall in `[week_start, week_start+6]` — app-layer, see below (M2) |
| `meal_slot_id` | `uuid` NOT NULL | **within-context FK → `meal_slot (household_id, meal_slot_id)`**, `ON DELETE RESTRICT` — the slot may be **soft-archived** but stays resolvable (M10). The one real cross-aggregate FK in this schema (DM-3 permits hard FKs within a context) |
| `ordinal` | `int` NOT NULL | position within the cell's stack (R-style, 1..n); contiguous among the cell's meals — app-layer (M2). Allocated on add, renumbered on remove/move |
| `attendees_override` | `uuid[]` null | per-instance attendance override (C5); **`NULL` = inherit the slot's `default_attendees`** (M4), `'{}'` = explicitly nobody/guests-only. Elements are identity `user_id` soft-refs — see Resolved call 1 |
| `reasoning` | `text` null | the AI snippet when this meal came from a proposal; null when hand-assigned |
| `note` | `text` null | free-text occupied-slot marker ("Takeout", "Out of town"); set ⇔ no `planned_dish` rows (M13). **Manual-only** (C16) |
| `source` | `text` NOT NULL | `CHECK (source IN ('manual','ai'))` — provenance; `manual` (J5) or `ai` (accepted suggestion, J4) |
| `created_by` / `updated_by` | `uuid` NOT NULL | attribution (soft-ref → identity user); who assigned / last edited the meal |
| `created_at` / `updated_at` | `timestamptz` | |

`UNIQUE (meal_plan_id, date, meal_slot_id, ordinal)` — a cell holds an **ordered stack of meals**; the `ordinal` disambiguates siblings so a member can have a separate meal from the rest (M2). Three rules **cannot** be expressed as single-row DB constraints and are enforced in the application services (the posture `recipe_ingredient` takes for its "≥ 1 ingredient" rule):

- **Date-in-week (M2):** `date ∈ [week_start, week_start+6]` — `week_start` lives on the parent row, so `AssignMeal` / `MoveMeal` check it, not a CHECK.
- **Cell ordinal contiguity (M2):** the meals in a `(date, slot)` cell are numbered `1..n` with no gaps — allocated on add, renumbered on remove/move in the aggregate (the posture `meal_slot` takes for its slot ordinals, M9).
- **Dishes-XOR-`note` (M13):** a persisted `planned_meal` is **either** a non-empty `planned_dish` set **or** a non-null `note`, never both, never neither — "has dishes" spans child rows, so the aggregate enforces it (a both-empty cell is simply not persisted). Note-meals carry no attendees, dishes, fulfillment, cost, or shopping contribution by construction.
- **Dish ordinal contiguity (M3):** enforced in the aggregate alongside the dish replace.

---

**`planned_dish`** — child of `PlannedMeal`; one dish (main, side, …) (C16)

| Column | Type | Notes |
|---|---|---|
| `planned_dish_id` | `uuid` PK | local to the aggregate |
| `household_id`, `planned_meal_id` | `uuid` | composite **FK → `planned_meal (household_id, planned_meal_id)`**, `ON DELETE CASCADE` |
| `recipe_id` | `uuid` null | soft ref → `recipes.recipe` (DM-20). **XOR** `product_id` |
| `product_id` | `uuid` null | soft ref → `catalog.product` (DM-10) — prepared food / future recipe-output leftover (C16). **XOR** `recipe_id` |
| `servings` | `int` NOT NULL | `CHECK (servings >= 1)` (M3); for a product dish this is the quantity in the product's default unit (C8) |
| `ordinal` | `int` NOT NULL | position within the meal (R-style); `UNIQUE (planned_meal_id, ordinal)` |

`CHECK (num_nonnulls(recipe_id, product_id) = 1)` — **exactly one** of recipe / product is set (M12). A **recipe** dish is cooked via the existing Recipes Cook flow and its fulfillment/cost come from Recipes' read models; a **product** dish resolves fulfillment from stock and cost from price directly (Inventory/Pricing). The product-meal **eat→consume** action is **deferred** (FUTURE.md, recipe-output products). No timestamps — the row's lifecycle is the parent meal's (wholesale replace on edit, like `recipe_ingredient`); full ordinal contiguity is the aggregate's job (M3).

---

**`meal_slot_config`** — aggregate root; the household's ordered meal-slot vocabulary (§7h, C4)

| Column | Type | Notes |
|---|---|---|
| `meal_slot_config_id` | `uuid` PK | UUIDv7; + `UNIQUE (household_id, meal_slot_config_id)` for child composite FKs |
| `household_id` | `uuid` | `UNIQUE (household_id)` — **one config per household** |
| `created_at` / `updated_at` | `timestamptz` | |

Seeded with default slots — **Breakfast / Lunch / Dinner** — at household creation, via the same per-household seeding hook as Catalog reference data (DM-9). Its relationship to `meal_plan` mirrors `tag`↔`recipe`: an independently-lifecycled vocabulary every week's plan references **by ID**.

---

**`meal_slot`** — child of `MealSlotConfig`; one configurable, ordered slot (C4)

| Column | Type | Notes |
|---|---|---|
| `meal_slot_id` | `uuid` PK | **stable across renames/reorders** so `planned_meal`s never orphan (M10); + `UNIQUE (household_id, meal_slot_id)` so `planned_meal` can FK it |
| `household_id`, `meal_slot_config_id` | `uuid` | composite **FK → `meal_slot_config (household_id, meal_slot_config_id)`**, `ON DELETE CASCADE` |
| `label` | `text` NOT NULL | free text (C4); non-blank + unique-per-household **among active slots** — app-layer (M9) |
| `ordinal` | `int` NOT NULL | order within a day; contiguous among active slots — app-layer (M9) |
| `default_attendees` | `uuid[]` | `NOT NULL DEFAULT '{}'`; members who normally eat this slot (C5). Elements are identity `user_id` soft-refs |
| `archived_at` | `timestamptz` null | **soft-archive** (M10) — an archived slot leaves the future grid but stays resolvable for historical `planned_meal`s; never hard-deleted while referenced |

`label`/`ordinal` uniqueness and contiguity are scoped to *active* (`archived_at IS NULL`) slots, which a plain `UNIQUE` can't express — enforced in `MealSlotConfig` (M9). A partial unique index `(household_id, label) WHERE archived_at IS NULL` is an available hardening but the app check is the source of truth.

---

**`user_preference`** — aggregate root; one member's dietary profile (C1, MP-O1)

| Column | Type | Notes |
|---|---|---|
| `user_preference_id` | `uuid` PK | UUIDv7; + `UNIQUE (household_id, user_preference_id)` for child composite FKs |
| `household_id` | `uuid` | tenancy (RLS) |
| `user_id` | `uuid` NOT NULL | the member (soft-ref → identity user); `UNIQUE (household_id, user_id)` — one profile per member (M6) |
| `created_at` / `updated_at` | `timestamptz` | |

Created lazily on first edit (J3). Modelled as a **per-`(household, user)` profile root** owning `TagStance` children — edited as a unit on the preferences screen, small and bounded by the household tag count (MP-O1).

---

**`tag_stance`** — child of `UserPreference`; one stance over one tag (C1)

| Column | Type | Notes |
|---|---|---|
| `tag_stance_id` | `uuid` PK | local to the aggregate |
| `household_id`, `user_preference_id` | `uuid` | composite **FK → `user_preference (household_id, user_preference_id)`**, `ON DELETE CASCADE` |
| `tag_id` | `uuid` NOT NULL | soft ref → `recipes.tag` (DM-20); `UNIQUE (user_preference_id, tag_id)` — one stance per tag (M6) |
| `stance` | `text` NOT NULL | `CHECK (stance IN ('Required','Preferred','Disliked','Restricted'))` — **no `Neutral`**: Neutral is the **absence** of a row (M6), so `SetStance(Neutral)` deletes |

`Required` / `Restricted` are the **hard** stances (bind the planner's own output, M5); `Preferred` / `Disliked` are **soft** (bias the score). The split between hard filter and soft score is the planner's two-tier optimization (M11, MP-O5) — not modelled in the schema, it lives in `MealConstraintResolver` (domain service).

---

## Read models (computed, never tables)

Per domain model §6/§7 — computed fresh at query time, **no storage**:

| Read model | Source |
|---|---|
| **MealConstraints** | `MealConstraintResolver` (domain): effective `AttendeeSet` = `attendees_override ?? meal_slot.default_attendees`, intersected with **current** household members; unions hard stances, averages soft stances across attendees (M4, MP-O4) |
| **MealFulfillment** | `PlanFulfillmentService` rolls up Recipes' `FulfillmentResult` (recipe dishes) at planned servings and `IInventoryStockReader` "in stock?" (product dishes) across a meal/week; note-meals contribute none |
| **MealCost** / **CostCompleteness** | `PlanCostingService`: `CostPerServing × servings` (recipe dishes, via `IRecipeReadModel`) + price × quantity (product dishes, via `IPriceReader`); deal-blind in Phase 3 (C7) |
| **PlanInsights** | `PlanInsightsService`: `UnusedExpiring` / `OverBudget` / `Repetition` / `UnfilledSlot` / `HardConflictResolved` — read-side, recomputed on every change, no new ports (C15 / J10) |
| **ProposedMeal** / pending-suggestion store | The AI ACL output (MP-O7). Validated typed suggestions held in a **transient, session-keyed store** (domain model §6) — **never persisted to `meal_planning`, never read by a domain query** (M7); only user-confirmed suggestions cross into `MealPlan`. There is **no `meal_plan_proposal` table** (the deliberate divergence from intake's persisted `import_session`, DM-15) |

---

## Cross-context references (by ID, no enforced FK — DM-3)

| Column | Points at (soft ref) |
|---|---|
| `planned_dish.recipe_id` | `recipes.recipe` |
| `planned_dish.product_id` | `catalog.product` |
| `tag_stance.tag_id` | `recipes.tag` |
| `user_preference.user_id`, `planned_meal.created_by` / `updated_by` | identity user |
| `meal_slot.default_attendees[]`, `planned_meal.attendees_override[]` (elements) | identity user |
| `*.household_id` | identity household |

The only **enforced** FK that crosses an *aggregate* boundary is `planned_meal.meal_slot_id` — and it stays **within** the `meal_planning` schema (DM-3 permits within-context FKs). "Shop for this week" writes through `shopping.AddItems` (DM-18, the P2-4 seam), never a direct table write (ADR-010). These soft-refs and the AI port become the §8 application-service ports in the App Services step.

---

## Resolved calls ✅

1. **Attendee sets are `uuid[]` array columns, not join tables.** `meal_slot.default_attendees` and `planned_meal.attendees_override` are small, unordered sets of household-member IDs. The array's decisive win is the override's three-state semantics — **`NULL` = inherit the slot default, `'{}'` = explicitly nobody, `'{a,b}'` = these members** (M4) — which a join table can't express without a denormalized `has_override` flag kept in sync with the rows. A join table's usual advantage (FK integrity) **does not apply**: `user_id` is a cross-context soft-ref to Identity with no enforced FK either way (DM-3), and M4's "intersected with current household members" is a read-time app filter regardless of storage. So the array is fewer tables, a simpler grid read, and a one-column `MoveMeal`. *Cost:* the first array column in the schema — a deliberate, localized deviation (`conventions.md` doesn't prohibit it; Npgsql maps `uuid[]` ↔ `List<Guid>` natively). **Upgrade trigger:** if a "every meal a given member attends" reverse query ever becomes first-class, it indexes naturally on a join table (`= ANY(array)` is awkward) — switch then.

2. **`meal_slot_id` is a real within-context FK; everything else is a soft-ref.** `planned_meal.meal_slot_id` → `meal_slot` is `ON DELETE RESTRICT` — safe because slots are **soft-archived** (`archived_at`), never physically removed (M10), exactly the Recipes "soft-delete keeps `cook_event` FK valid" situation (DM-20). Every cross-*context* reference (`recipe_id`, `product_id`, `tag_id`, `user_id`, `household_id`) stays an unenforced soft-ref (DM-3).

3. **No proposal table — the AI staging is transient (MP-O7).** Unlike intake's persisted `import_session` (DM-15), meal suggestions are cheap to regenerate and reviewed inline in one sitting, so the validated `ProposedMeal`s live in a **transient, session-keyed pending store** — quarantined from the schema and every domain read (ADR-007). Quarantine is about the *domain boundary*, not the disk; "never persisted to `meal_planning`" satisfies it strongly. *Consequence for events:* there is **no `MealPlanProposalAccepted`** — accept emits per-cell `MealPlanned(source: ai)`. **Upgrade trigger:** if suggestions must survive a refresh or be shared across members mid-review, promote to a durable household-keyed proposal aggregate — a TTL-and-table change, not a re-model.

4. **`planned_meal` carries `source` + attribution.** `source` (`manual` | `ai`) is persisted rather than inferred from `reasoning` being non-null (an AI meal can legitimately have a null snippet, so inference is lossy); `created_by` / `updated_by` give reliable attribution for the analytics substrate (C2), mirroring `cook_event.cooked_by`. The `MealPlanned` event still carries `source` + `by` for the event stream.

5. **Cross-row invariants live in app services, not DB CHECKs.** M13 (dishes-XOR-`note`), M2 (date-in-week + cell ordinal contiguity), M3 (dish ordinal contiguity), and M9 (active-slot label uniqueness + ordinal contiguity) each span multiple rows or reference the parent's columns, which a single-row CHECK can't express — they are enforced in `AssignMeal` / `MoveMeal` / `MealSlotConfig`, exactly as Recipes enforces "≥ 1 ingredient" and ordinal contiguity in `ReplaceIngredients`. The single-row invariants (M1, M2-uniqueness incl. `ordinal`, M3-servings, M6, M12) **are** DB constraints.

6. **A cell holds a stack of meals, not one (MP-O8 — reverses the original M2).** The key is `UNIQUE (meal_plan_id, date, meal_slot_id, ordinal)`, not `(…, meal_slot_id)`: a `(date, slot)` cell holds an **ordered stack of 0..n `planned_meal`s**, each with its own dishes/attendees/fulfillment/cost. **Why:** dishes within one meal share a single meal-level attendee set and so cannot express *separate meals for separate people in the same slot* (Mike's meal vs Jane's, each with its own rollup) — the prototype's `CellStack` shape, now realigned in the schema. *Consequences:* `AssignMeal(…, mealId?)` adds-or-updates (no upsert-by-cell, closing the "Add meal" silent-overwrite path); `ClearMeal(mealId)` renumbers siblings; `MoveMeal` **relocates into the target stack — no swap**, retiring the raw-SQL swap path and the deferrable-constraint workaround. See domain model **MP-O8**; filed as **plantry-5eh**.

---

> **RLS.** Every table carries `household_id` and a per-household row-level-security policy (ADR-008 / DM-1), including all child tables (`planned_meal`, `planned_dish`, `meal_slot`, `tag_stance`). Tenant-safe child FKs use the `(household_id, parent_id)` composite pattern (conventions.md) against the parent's `UNIQUE (household_id, id)`. **The new `MealPlanningDbContext` must be registered in `RlsMiddleware.InvokeAsync`** when first read over HTTP — omitting it leaves `_householdId` empty so the query filter returns nothing (the Recipes P2-1 gotcha).

> **ADR-010 reconciliation.** This schema refines ADR-010's "Slot" model — see domain model §12 (h): the recurring definition is `meal_slot` (in `meal_slot_config`), the instance is `planned_meal` referencing the slot **by ID**; a meal holds **multiple `planned_dish`es** (C3) each a recipe-XOR-product (C16); slots carry **default attendees** and meals an **override** (C5); the `MealPlanProposal` *aggregate* becomes a **transient pending store** reviewed inline (MP-O7). `UserPreference`/`Stance` graduates here from FUTURE.md (C1).

> **Feeds the next step.** The §8 ports — `IRecipeReadModel`, `ITagReader`, `ICatalogProductReader`, `IInventoryStockReader`, `IPriceReader`, `IShoppingListWriter`, `IHouseholdMemberReader`, `IMealPlanner` — become the application-service interfaces wired in the **App Services** pass, sequenced by [PHASE-3-PLAN.md](../../PHASE-3-PLAN.md). `IInventoryStockReader` and `IShoppingListWriter` already exist (Recipes / P2-4) and are reused; `IMealPlanner` (the untrusted planner, ADR-007) is new, implemented in `Plantry.MealPlanning.Infrastructure` over the household AI key (DM-7) like Intake wraps its `ChatClient`.
