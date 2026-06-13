# Recipes (`recipes` schema) — Phase 2 🔲

> Renders [`DomainDesign/recipes-domain-model.md`](../DomainDesign/recipes-domain-model.md) into tables. Authority for *rationale* is the domain model + ADRs; this file holds the *shape*. Recipes is a **downstream consumer** of every Phase-1 context — it references Catalog, Inventory, Pricing, Shopping, and Identity **by ID only** (DM-3), with hard FKs only **within** the `recipes` schema.

`Recipe` is the aggregate root with `Ingredient` children (`recipe_ingredient`) and a 1:1 `recipe_photo`; `Tag` is its own small root with a `recipe_tag` membership join; `CookEvent` is an append-only root. `FulfillmentResult` and `CostPerServing` are **computed read-side — never tables** (domain model §6).

---

**`recipe`** — aggregate root; the household's canonical definition of a dish

| Column | Type | Notes |
|---|---|---|
| `recipe_id` | `uuid` PK | UUIDv7; + `UNIQUE (household_id, recipe_id)` for child composite FKs |
| `household_id` | `uuid` | tenancy (RLS) |
| `name` | `text` | required; `UNIQUE (household_id, name)` (R1 / C4) |
| `source` | `text` null | free text — cookbook, URL, anything (C5) |
| `cook_time_minutes` | `int` null | optional |
| `default_servings` | `int` | required; `CHECK (default_servings >= 1)` (R2) |
| `directions` | `text` null | single field (C13); paragraphs = derived `Step`s, `#` line = section reset, **at render**. Steps are **not** persisted as rows |
| `archived_at` | `timestamptz` null | **soft delete** — see Resolved call 1 |
| `created_at` / `updated_at` | `timestamptz` | |

Backing indexes for Browse sort (J2): the `UNIQUE (household_id, name)` index serves Name; `(household_id, created_at)` serves "Recently added"; `(household_id, cook_time_minutes)` serves Cook time. **Fulfillment** and **Cost** sorts are cross-context computed values — not stored, not indexable here; the read layer sorts them after composing live Inventory/Pricing reads (Resolved call 6).

---

**`recipe_ingredient`** — ordered child of `Recipe`; one required item per row

| Column | Type | Notes |
|---|---|---|
| `ingredient_id` | `uuid` PK | local to the aggregate; **re-minted on every save** (O1) — see Resolved call 2 |
| `household_id`, `recipe_id` | `uuid` | composite **FK → `recipe (household_id, recipe_id)`**, `ON DELETE CASCADE` (within-context, enforced) |
| `product_id` | `uuid` NOT NULL | soft ref → `catalog.product` — **never null** (R4 / C12). May be a *parent product* (DM-19), resolved to a variant at cook time |
| `quantity` | `numeric(12,3)` null | null **only** for an untracked staple ("to taste") — R5 |
| `unit_id` | `uuid` null | soft ref → `catalog.unit`; null only for an untracked staple — R5 |
| `group_heading` | `text` null | optional section label, e.g. "Salad", "Dressing" (C6 / N4) |
| `ordinal` | `int` NOT NULL | position within the recipe (R6) |

`CHECK ((quantity IS NULL) = (unit_id IS NULL))` — quantity and unit are **both set or both null** (R5). The narrower rule that null is permitted *only* for an untracked product (`track_stock = false`) needs a Catalog read and is enforced in the `AuthorRecipe` service, not the DB. Likewise R7 (a tracked unit must have a conversion path to the product's unit) is an app-layer check.

`UNIQUE (recipe_id, ordinal)` prevents duplicate positions; full **contiguity** of ordinals (R6) is enforced app-side in `ReplaceIngredients`. No timestamps — the row's lifecycle is the parent's (wholesale delete + re-insert per save).

---

**`recipe_photo`** — 1:1 with `recipe`; the photo bytes kept **off the hot recipe row** (mirrors `intake.import_receipt`)

| Column | Type | Notes |
|---|---|---|
| `recipe_id` | `uuid` PK | also composite **FK → `recipe (household_id, recipe_id)`**, `ON DELETE CASCADE` |
| `household_id` | `uuid` | tenancy (RLS) |
| `content` | `bytea` | the image bytes (ADR-009 — binaries in Postgres); Postgres TOASTs it out-of-line so it never bloats Browse-list reads |
| `content_type` | `text` | e.g. `image/jpeg` |
| `sha256` | `bytea` null | integrity / dedupe |
| `created_at` / `updated_at` | `timestamptz` | |

`SetPhoto` upserts this row; `RemovePhoto` deletes it. Browse (J1/J2) and Inspect (J3) load `recipe` without joining `recipe_photo` until the hero image is actually needed — see Resolved call 3.

---

**`cook_event`** — append-only root; an immutable record that a recipe was cooked (C3)

| Column | Type | Notes |
|---|---|---|
| `cook_event_id` | `uuid` PK | UUIDv7; the `source_ref` Inventory's `Consume` stamps on every resulting journal row |
| `household_id` | `uuid` | tenancy (RLS) |
| `recipe_id` | `uuid` | within-context **FK → `recipe (household_id, recipe_id)`**, `ON DELETE RESTRICT` — safe because recipe is soft-deleted, never physically removed |
| `servings_cooked` | `int` | the materialized `ServingsScale × default` at cook time; `CHECK (servings_cooked >= 1)` (R2) |
| `cooked_by` | `uuid` NOT NULL | attribution (O2); soft ref → identity user. Append-only ⇒ unrecoverable if not captured at write time |
| `cooked_at` | `timestamptz` | |

**No `updated_at`, no delete** — rows are never updated or deleted (R8), matching `inventory.stock_journal_entry`. Index `(household_id, recipe_id, cooked_at)` for future history / frequency reads.

---

**`tag`** — household-scoped, **kind-less** vocabulary entry, referenced by ID (C2)

| Column | Type | Notes |
|---|---|---|
| `tag_id` | `uuid` PK | UUIDv7; + `UNIQUE (household_id, tag_id)` for the membership-join composite FK |
| `household_id` | `uuid` | tenancy (RLS) |
| `name` | `text` | required; `UNIQUE (household_id, name)` |
| `category` | `text` null | cosmetic enum — `CHECK (category IN ('Diet','Protein','Flavor','Cuisine'))`; **no planner meaning** (C2) |
| `created_at` / `updated_at` | `timestamptz` | |

Eight defaults seeded at household creation (Vegetarian, Vegan, Dairy-Free, Gluten-Free; Meat, Poultry, Fish; Spicy) — same per-household seed mechanism as Catalog reference data (DM-9). New tags minted inline while authoring (J6) via the same write-port shape as inline untracked-staple creation. **No `Stance` / strength / polarity column** — that is a future Meal-Planning `UserPreference` on the User↔Tag edge (N5), built kind-less now so it lands with no migration.

---

**`recipe_tag`** — membership join; child of the `Recipe` aggregate (the tag *set* it owns)

| Column | Type | Notes |
|---|---|---|
| `household_id` | `uuid` | tenancy (RLS); shared key of both composite FKs below |
| `recipe_id` | `uuid` | composite **FK → `recipe (household_id, recipe_id)`**, `ON DELETE CASCADE` |
| `tag_id` | `uuid` | composite **FK → `tag (household_id, tag_id)`**, `ON DELETE RESTRICT` |

PK `(recipe_id, tag_id)`. `SetTags` replaces the set (delete + insert for the recipe). Reverse index `(household_id, tag_id)` powers "filter recipes by tag" (J2). `ON DELETE RESTRICT` on `tag_id` is conservative — tag deletion is not a modelled behaviour (only `Create`/`Rename`/`SetCategory`); a future delete-tag feature decides cascade-vs-block then.

---

## Read models (computed, never tables)

Per domain model §6 — these are computed fresh at query time and get **no storage**:

| Read model | Source |
|---|---|
| **FulfillmentResult** / **IngredientStatus** | `FulfillmentService` over live `inventory.product_stock` via `IInventoryStockReader`. Untracked → always satisfied; parent product → sums stock across all variant children (DM-19). Flags ingredients expiring ≤ 4 days |
| **CostPerServing** / **CostCompleteness** | `CostingService` over `pricing.price_observation` via `IPriceReader` + `IUnitConverter`. `Full`/`Partial`/`None` computed from priced-vs-costable counts; untracked staples excluded from the costable set |
| **ServingsScale** | `desired ÷ default_servings`, applied client-side at view time (J3 step 5), materialized into `cook_event.servings_cooked` at cook time |
| **IngredientResolution** | Transient cook-time input (Variant Disambiguation Picker output, C7/C11); consumed by `CookRecipe`, never persisted |

---

## Cross-context references (by ID, no enforced FK — DM-3)

| Column | Points at (soft ref) |
|---|---|
| `recipe_ingredient.product_id` | `catalog.product` |
| `recipe_ingredient.unit_id` | `catalog.unit` |
| `cook_event.cooked_by` | identity user |
| `*.household_id` | identity household |

Cook writes go through `inventory.Consume` (ADR-011) and "add missing" through `shopping.AddItems` (DM-18) — **never** direct table writes (ADR-010). These become the §8 application-service ports in the App Services step.

---

## Resolved calls ✅

1. **Recipe is soft-deleted (`archived_at`), not hard-deleted.** `cook_event.recipe_id` is an enforced, append-only within-context FK (R8), so a recipe with cook history cannot be physically removed — exactly the Catalog "a product referenced by years of journal history can't be hard-deleted" situation (DM-4). Soft-delete keeps every `cook_event` FK valid and lets the `ON DELETE RESTRICT` on it stay a never-fired backstop. *Rejected:* hard-delete + null/cascade on cook_event (loses or orphans immutable history) and a no-delete-at-all policy (users do remove recipes).

2. **`recipe_ingredient` is wholesale-replaced with fresh IDs.** Edit deletes the recipe's ingredient rows and re-inserts the new ordered list (O1 / J7); `ingredient_id`s are re-minted. Nothing outside the aggregate quotes an `ingredient_id` (shopping uses `recipe_id`, the cook journal uses `cook_event_id`), so no external contract breaks. The `ON DELETE CASCADE` on the composite FK also makes recipe soft-delete-then-purge (if ever added) clean. **Upgrade trigger:** per-line history/notes/ratings would switch this to diff-and-preserve — a contained `ReplaceIngredients` change.

3. **Photo in a 1:1 `recipe_photo` child, off the hot row.** Browse loads *all* household recipes (J1) and must stay lean; keeping `bytea` in a separate TOAST-backed table means list/detail reads never drag image bytes unless the hero image is requested. Mirrors `intake.import_receipt` (DM-15). *Rejected:* a `content bytea` column directly on `recipe` (bloats the most-read table).

4. **Directions is one `text` column — no `recipe_step` table (C13).** `Step`s and section headings are *derived* at render by splitting on paragraph / `#` boundaries. This keeps the schema one column and makes URL recipe import (FUTURE.md) and a future guided cook mode clean targets. **Promotion trigger:** per-step metadata (timers, durations) becomes the reason to migrate to `recipe_step` child rows — deferred until that feature is real.

5. **Computed value objects get no tables.** `FulfillmentResult` and `CostPerServing` are always fresh from live Inventory / Pricing reads (domain model §1) — caching them on `recipe` would let the Browse view lie about pantry state. They live entirely in the read layer.

6. **Browse-sort indexing splits local vs cross-context.** Name / Cook time / Recently-added sort on local `recipe` indexes; Fulfillment and Cost sort on values composed from other contexts at read time, so they carry **no index in this schema** and are ordered in the application/read-model layer after the cross-context reads.

---

> **RLS.** Every table carries `household_id` and a per-household row-level-security policy (ADR-008 / DM-1), including `recipe_tag` and `recipe_photo`. Tenant-safe child FKs use the `(household_id, parent_id)` composite pattern (conventions.md) against the parent's `UNIQUE (household_id, id)`.

> **DM-19 / C11 reconciliation.** The Variant Disambiguation Picker is a cook-time read concern with no table here; it shows **all** variants with unit-incompatible ones visible-but-disabled (C11 supersedes DM-19's "excluded"). Already recorded on the DM-19 line of [index.md](index.md).

> **Feeds the next step.** The §8 ports (`ICatalogProductReader`, `ICatalogWriter`, `IUnitConverter`, `IInventoryStockReader`, `IInventoryConsumer`, `IPriceReader`, `IShoppingListWriter`) become the application-service interfaces wired in the **App Services** pass.
