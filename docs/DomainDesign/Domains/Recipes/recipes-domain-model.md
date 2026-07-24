# Recipes — Domain Model

> **Status:** Modeling calls O1–O3 resolved; costing refined — Phase 2. Ready for the Data Schema pass.
>
> **Purpose:** Translate the confirmed [ubiquitous language](recipes-ubiquitous-language.md) into aggregate boundaries, invariants, behaviours, value objects, and the cross-context ports the Recipes context needs. This is the contract the Data Schema step renders into the `recipes` schema and the App Services step implements. Terms here appear **verbatim** in the language doc.
>
> **Bounded context:** Recipes (`recipes` schema, Phase 2). References Catalog, Inventory, Pricing, Shopping, Identity **by ID only** — no enforced cross-context FKs (DM-3).
>
> **Code shape:** aggregates follow the established pattern — `AggregateRoot<TId>` with strongly-typed IDs, private setters, factory `Create`/`Start`, `IClock`-stamped mutators, `Result<T>`/`Error` for failable operations (see `Plantry.Catalog.Domain.Product`, `Plantry.Inventory.Domain.ProductStock`).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## 1. Context boundary & dependency rules

Recipes is a **downstream consumer** of every Phase-1 context. It owns three aggregates and reaches everything else through ports (§8).

| Rule | Statement |
|---|---|
| **Ownership** | Recipes owns `Recipe` (+ its `Ingredient` children), `CookEvent`, and `Tag`. Nothing else. |
| **Reference by ID** | `product_id`, `unit_id`, `tag` targets in preferences, `shopping_list` rows, `user_id`, `household_id` are all soft-refs — IDs, never object graphs, never FKs across schemas (DM-3). |
| **Reads are always fresh** | `FulfillmentResult` and `CostPerServing` are **never** persisted on `Recipe`. They are computed at query time from live Inventory / Pricing reads (cross-cutting note, J3). |
| **Writes go through primitives** | Cook never touches stock tables; it calls Inventory's single `Consume` primitive (ADR-011). "Add missing" never touches the shopping list directly; it calls Shopping's `AddItems` (DM-18). |
| **Same-context FKs allowed** | `Recipe`↔`Ingredient`, `Recipe`↔`Recipe`-`Tag` membership, and `CookEvent`→`Recipe` are all inside the `recipes` schema, so they *may* use real FKs (DM-3 permits hard FKs within a context). |

---

## 2. Aggregate map

| Aggregate root | Identity | Owns (composition) | References by ID | Lifecycle |
|---|---|---|---|---|
| **Recipe** | `RecipeId` | ordered `Ingredient[]` | `HouseholdId`, `TagId[]` (membership), each `Ingredient.ProductId` / `UnitId` (Catalog) | Mutable; ingredient list replaced wholesale on edit (J7) |
| **CookEvent** | `CookEventId` | — | `RecipeId`, `HouseholdId`, `CookedBy` (User) | **Append-only / immutable** once written (C3) |
| **Tag** | `TagId` | — | `HouseholdId` | Mutable label; minted inline or seeded; kind-less (C2) |

`Ingredient` is an **entity local to the `Recipe` aggregate** (not a root): it has identity *within* a loaded recipe (so reorder and per-line status work) but is never referenced from outside the aggregate. See open call **O1**.

`Tag` is modelled as its **own small root**, not owned by `Recipe`, because a tag has an independent lifecycle (created once, reused across many recipes, later read by the meal planner). `Recipe` holds a **set of `TagId` memberships**, not `Tag` entities (C2). The tag vocabulary is **owned by the Recipes context** (the `recipe_tag` membership join lives here too) — see O3 for why it is *not* a separate context or a Catalog citizen.

---

## 3. Recipe aggregate

### 3.1 Recipe (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `RecipeId` | |
| `HouseholdId` | `HouseholdId` | tenancy |
| `Name` | `string` | required; **unique per household** (C4 → R1) |
| `Source` | `string?` | free text — cookbook, URL, anything (C5) |
| `Photo` | binary content ref | optional; stored per ADR-009 (Postgres bytea / content table) |
| `CookTimeMinutes` | `int?` | optional |
| `DefaultServings` | `int` | required, ≥ 1 (R2) |
| `Directions` | `string` | single text field; paragraphs = derived `Step`s, `#` line = section reset (C13). Steps are **not** persisted as rows. |
| `Tags` | `IReadOnlyList<TagId>` | membership set; plain, no strength/polarity (C2) |
| `Ingredients` | `IReadOnlyList<Ingredient>` | ordered; ≥ 1 to save (R3) |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | |

**Behaviours**

| Method | Effect |
|---|---|
| `Recipe.Create(householdId, name, defaultServings, clock)` | Factory. Validates name non-blank, servings ≥ 1. Emits **RecipeCreated**. |
| `Rename(name, clock)` | Re-validates non-blank; uniqueness is a cross-aggregate check the app layer makes (R1). |
| `SetSource / SetCookTime / SetPhoto / RemovePhoto / SetDirections(...)` | Field mutators, each `Touch`es `UpdatedAt`. |
| `SetTags(IReadOnlyList<TagId>, clock)` | Replaces the membership set. |
| `ReplaceIngredients(orderedLines, clock)` | **Wholesale replace** of the ordered ingredient list (J7). Re-validates R3–R6. Emits **RecipeUpdated**. |
| `ChangeDefaultServings(newServings, ScaleMode, clock)` | Sets servings; `ScaleMode.Proportional` multiplies every stored ingredient quantity by `new ÷ old`, `ScaleMode.Keep` leaves them (J7 step 3). |

> Authoring-time **unit-mismatch** validation (C10) and **inline untracked-staple creation** (C12) are *not* Recipe methods — they need Catalog reads/writes and live in the `AuthorRecipe` application service (§7), which assembles validated `Ingredient`s before calling `ReplaceIngredients`.

### 3.2 Ingredient (entity, child of Recipe)

| Field | Type | Notes |
|---|---|---|
| `Id` | `IngredientId` | local to the aggregate; addressable while loaded, **re-minted on each save** (O1 ✅) |
| `ProductId` | `ProductId` | soft-ref, **never null** (C12 → R4). May be a *parent product* (DM-19), resolved at cook time. |
| `Quantity` | `decimal?` | null only for an untracked staple ("to taste") — R5 |
| `UnitId` | `UnitId?` | null only for an untracked staple — R5 |
| `GroupHeading` | `string?` | optional section label, e.g. "Salad", "Dressing" (C6 / N4) |
| `Ordinal` | `int` | position within the recipe (R6) |

Ingredient is a leaf with no behaviour of its own — it is constructed validated and only ever replaced wholesale by its parent `Recipe`. It does **not** carry status or cost; those are computed value objects (§6), not stored fields.

---

## 4. CookEvent aggregate

An immutable record that a recipe was cooked — the append-only substrate for future history/frequency features (C3). Lives in the `recipes` schema; `RecipeId` may be a real FK (same context).

| Field | Type | Notes |
|---|---|---|
| `Id` | `CookEventId` | |
| `HouseholdId` | `HouseholdId` | |
| `RecipeId` | `RecipeId` | |
| `ServingsCooked` | `int` | the materialized `ServingsScale × default` at cook time (≥ 1) |
| `CookedBy` | `UserId` | attribution (O2 ✅). Inventory's `Consume` already stamps a user on every journal row; CookEvent matches it. Append-only ⇒ unrecoverable if not captured at write time. |
| `CookedAt` | `DateTimeOffset` | |

**Behaviours:** `CookEvent.Record(recipeId, householdId, servingsCooked, cookedBy, clock)` — the only factory. **No mutators, no delete.** Writing one is part of the Cook orchestration (§7), which emits **RecipeCooked**.

---

## 5. Tag aggregate

A household-scoped, **kind-less** vocabulary entry, referenced by ID (C2). Eight defaults seeded at household creation (Vegetarian, Vegan, Dairy-Free, Gluten-Free, Meat, Poultry, Fish, Spicy).

| Field | Type | Notes |
|---|---|---|
| `Id` | `TagId` | |
| `HouseholdId` | `HouseholdId` | |
| `Name` | `string` | required; **unique per household** |
| `Category` | `TagCategory?` | cosmetic enum — `Diet` / `Protein` / `Flavor` / `Cuisine`; **no planner meaning** (C2) |

**Behaviours:** `Tag.Create(householdId, name, category?, clock)` (inline minting from the editor, J6), `Rename`, `SetCategory`.

> **Stance lives elsewhere.** A tag carries no hard/soft force. The per-member **Stance** (Required · Preferred · Neutral · Disliked · Restricted, N5) is a future Meal-Planning `UserPreference` on the User↔Tag edge — out of scope here. Building `Tag` kind-less now means that lands with no migration.
>
> **Placement (O3 ✅):** the tag vocabulary is **owned by the Recipes context** — not a separate "vocabulary" context (too thin to justify) and not Catalog (whose cohesion is *physical-goods master data*, which tags are not; and whose `DbContext` is already the codebase's unhealthiest file). Tags are minted and applied here in Phase 2, so ownership sits where the concept lives. The future meal planner is **downstream** of Recipes — it reads `tag` / `recipe_tag` / recipes by ID (the natural direction), and owns its own `UserPreference`/`Stance`. Because everything references `tag_id` by ID, extracting the vocabulary into a dedicated context later (if ever warranted) is a bounded, ID-preserving move. See O3 in §11.

---

## 6. Value objects (computed, never persisted)

| VO | Shape | Rules |
|---|---|---|
| **IngredientStatus** | enum: `InStock` / `Low` / `Missing` / `Untracked` | Per-ingredient availability (N2). `Untracked` (`track_stock = false`) is **always satisfied** — never `Missing`/`Low` (C12). |
| **FulfillmentResult** | `{ overall, IngredientFulfillment[] }` where each is `{ IngredientId, IngredientStatus, ExpiresWithinDays?, AvailableQuantity? }` | Computed fresh per recipe at a given servings count. Parent-product ingredient **sums stock across all variant children** (DM-19). Flags ingredients expiring ≤ 4 days ("Use soon", J1/J3). |
| **CostPerServing** | `{ Amount?, Completeness, PricedCount, CostableCount, MissingPriceProductIds[] }` | From `PriceObservation` history (Pricing). **`Completeness`** is a three-state enum computed from real data (see below) — it is a property of the *computation*, not a UI guess. **Untracked staples are excluded from `CostableCount`** (no price by design ⇒ not a gap). **Parent-product ingredient prices from the cheapest converted line cost across its live variant children** (DM-19, symmetric with the Fulfillment stock rollup) — a price observation only ever lands against the concrete variant actually purchased, never the abstract parent, so a parent line runs the price → unit-price → conversion pipeline once per variant (conversion keyed on that variant's own product id) and takes the minimum of the successful candidates. A parent with zero live variants, or none of whose variants yield a usable/convertible price, is un-priced and contributes its **own** id (never a variant id) to `MissingPriceProductIds`. |
| **CostCompleteness** | enum: `Full` / `Partial` / `None` | **`Full`** — every costable ingredient priced (`PricedCount == CostableCount > 0`) ⇒ exact `Amount`. **`Partial`** — some priced, some not (`0 < PricedCount < CostableCount`) ⇒ `Amount` is a clearly-flagged **under-estimate**; UI shows the count and may surface `MissingPriceProductIds` as a nudge to capture a price. **`None`** — nothing priced ⇒ `Amount` is null and the figure is **omitted entirely**, never shown as zero (J3). |
| **ServingsScale** | `{ ratio = desired ÷ DefaultServings }` | Applied to ingredient quantities at view time (client-side, J3 step 5) and materialized into `ServingsCooked` at cook time (J4). |
| **IngredientResolution** | `{ IngredientId, Allocation[] }` where each is `{ variantProductId, quantity, unitId }` | **Transient cook-time input** (the Variant Disambiguation Picker output, C7/C11). Captures the per-variant split for a parent-product ingredient. Not persisted — consumed by the Cook orchestration, then discarded. |

---

## 7. Domain & application services

These coordinate the aggregates with cross-context ports (§8). None of them lives *on* `Recipe` — keeping the aggregate pure of Inventory/Pricing/Catalog knowledge.

| Service | Responsibility | Touches |
|---|---|---|
| **FulfillmentService** (domain) | `Compute(recipe, servings) → FulfillmentResult`. Untracked → satisfied; tracked → compare available vs scaled required; parent → roll up variants (DM-19). | `IInventoryStockReader` |
| **CostingService** (domain) | `Compute(recipe, servings) → CostPerServing`. Counts costable ingredients (tracked, real product), sums those with price history, and sets `Completeness` (`Full`/`Partial`/`None`) + the missing-price list accordingly. **Parent-product ingredient prices from the cheapest converted line cost across its live variant children** (DM-19) — mirrors `FulfillmentService`'s stock rollup. | `IPriceReader`, `IUnitConverter`, `ICatalogProductReader` |
| **AuthorRecipe** (application) | Create/Edit (J6/J7). Resolves typed product per line (search → select, or inline-create untracked staple, C12); validates unit→product conversion path, surfacing the **inline `ProductConversion`** form when missing and writing it to Catalog on save (C10); enforces R1–R6; calls `Recipe.Create` / `ReplaceIngredients`. | `ICatalogProductReader`, `ICatalogWriter`, `IUnitConverter` |
| **CookRecipe** (application) | The J4 flow. Applies `ServingsScale`; takes the user's `IngredientResolution[]` (overrides, swaps, skips — full CRUD, C9); for each tracked, not-skipped line calls `Consume(qty, unit, reason="Recipe", sourceRef=cookEventId)` (ADR-011) — **consuming whatever is available, never blocking on shortfall** (C8); **skips untracked** (C12); writes the `CookEvent`; emits **RecipeCooked**. | `IInventoryConsumer`, `ICatalogProductReader` |
| **AddMissingToShoppingList** (application) | J5. From a fresh `FulfillmentResult` at the displayed servings, takes `Missing` lines (excludes untracked), and calls Shopping `AddItems(product_id, scaledQty, unit_id, source="recipe", source_ref=recipeId)`. Merge/no-dup is Shopping's concern (DM-18). | `IShoppingListWriter`, `IInventoryStockReader` |

**Cook ordering & atomicity (note for the schema/app step):** `CookRecipe` writes the `CookEvent` and performs the consumes in one transaction so a partial cook can't lose its journal record; the `sourceRef` on each `Consume` is the `CookEventId`, tying every stock movement back to the cook that caused it.

---

## 8. Cross-context ports (anti-corruption layer)

Recipes depends on these interfaces; the contexts that own the data implement them. All traffic is by ID (DM-3).

| Port | Used by | Surface (read = R / write = W) |
|---|---|---|
| **ICatalogProductReader** | Author, Cook, Fulfillment, Costing | R: product name, `track_stock`, `default_unit_id`, parent/variant tree (DM-10, DM-19) |
| **ICatalogWriter** | Author | W: inline-create untracked staple (C12); add `ProductConversion` (C10) |
| **IUnitConverter** | Author, Costing | R: resolve a quantity between units for a product; fail loudly when no path (DM-12) |
| **IInventoryStockReader** | Fulfillment, AddMissing | R: available quantity + soonest expiry per product (and per variant for rollup) (DM-13) |
| **IInventoryConsumer** | Cook | W: the single `Consume` primitive (ADR-011) |
| **IPriceReader** | Costing | R: latest/representative `PriceObservation` per product **or live variant child** (DM-17, DM-19) |
| **IShoppingListWriter** | AddMissing | W: `AddItems` with provenance (DM-18) |

---

## 9. Domain events

| Event | Payload | Emitted by |
|---|---|---|
| **RecipeCreated** | `recipeId, householdId, at` | `Recipe.Create` (J6) |
| **RecipeUpdated** | `recipeId, householdId, at` | `Recipe.ReplaceIngredients` / field edits on save (J7) |
| **RecipeCooked** | `recipeId, householdId, servingsCooked, cookedBy, at` | `CookRecipe` after the `CookEvent` is written and consumes complete (J4). `cookedBy` mirrors `CookEvent.CookedBy` (O2). |

---

## 10. Invariants (consolidated)

| # | Invariant | Source | Enforced |
|---|---|---|---|
| **R1** | `Name` unique per `(household_id)` | C4 | App check + DB `UNIQUE` |
| **R2** | `DefaultServings ≥ 1`; `ServingsCooked ≥ 1` | J6 | Aggregate ctor/mutator |
| **R3** | A saved `Recipe` has ≥ 1 `Ingredient` | J6 | `ReplaceIngredients` |
| **R4** | Every `Ingredient.ProductId` is non-null | C12 | Aggregate |
| **R5** | `Quantity` and `UnitId` are **both set or both null**; null is permitted **only** when the product is an untracked staple (`track_stock = false`) | C12 | Author service (needs Catalog read) |
| **R6** | Ingredient `Ordinal`s form a contiguous order within the recipe; `GroupHeading` is free-form optional | C6 | `ReplaceIngredients` |
| **R7** | A tracked ingredient whose `UnitId` has no conversion path to the product's unit **blocks save** — unless the author supplies an inline `ProductConversion` (C10) | C10 | Author service |
| **R8** | `Ingredient` list is replaced wholesale on edit; `CookEvent` is immutable and never deleted | J7, C3 | Aggregate design |
| **R9** | At cook time a parent-product ingredient resolves to ≥ 1 variant; if none, it is `Missing`. Split allocations *target* the required quantity but **never block** — consume what's available (C8 supersedes "must equal required") | DM-19, C8, C11 | Cook service |

---

## 11. Resolved modeling calls

The three spots the language/journeys didn't fully pin — each now decided.

- **O1 — Ingredient identity across edits ✅.** `Ingredient` is an entity with a **local `IngredientId`** so it's addressable *while a recipe is loaded* (reorder, per-line status, mapping a line to its cook-time variant choice). Edit is a **true wholesale replace** (J7): on save the rows are deleted and re-inserted with fresh IDs. Nothing outside the aggregate quotes an `IngredientId` (shopping uses `recipeId`, the cook journal uses `cookEventId`), so no external contract is broken. *This is distinct from the `ProductId` soft-ref each line carries — that's a separate, settled concern (C12/R4); O1 is only about the line's own handle.* **Upgrade trigger:** if a future feature needs an ingredient line's identity to survive edits (per-line history/notes/ratings), switch to diff-and-preserve then — a contained change to `ReplaceIngredients` plus the editor echoing IDs back.

- **O2 — `CookEvent.CookedBy` ✅.** Store it. Decisive reason: `CookEvent` is append-only, so attribution not captured at write time is **permanently unrecoverable** — while the cost to capture it is one column + a `UserId` already in the request context. It also keeps CookEvent consistent with the Inventory journal, which already stamps the consuming user. `cookedBy` is added to the `RecipeCooked` payload too (§9).

- **O3 — Tag vocabulary placement ✅.** **Owned by the Recipes context**, alongside the `recipe_tag` membership join. Rejected alternatives: a *dedicated vocabulary context* (a one-aggregate context is more ceremony than the boundary warrants in Phase 2) and *Catalog* (its cohesion is physical-goods master data — tags aren't goods — and its `DbContext` is already the codebase's least-healthy file). Ownership sits where tags are minted and applied; the future meal planner is **downstream** and reads by ID (the natural dependency direction), owning its own `UserPreference`/`Stance`. Inline tag-minting (J6) uses a write-port exactly like inline untracked-staple creation uses `ICatalogWriter` (C12). Everything references `tag_id` by ID, so a later extraction — should a second writer or standalone tag-management feature ever appear — is a bounded, ID-preserving move.

---

## 12. Reconciliation note (carried from the language doc)

**DM-19 vs C11 — resolved.** DM-19's original "unit-incompatible variants are excluded, not substituted" is **superseded by C11**: the Variant Disambiguation Picker shows **all** variants, with unit-incompatible ones **visible but disabled** (labelled), not hidden. The decision log already records this amendment on the DM-19 line of `DataModels/index.md`; R9 above and §6 `IngredientResolution` encode the C11 behaviour. No further edit required.

---

## Feeds the next step

The **Data Schema** pass renders this into the `recipes` schema: `recipe` (root) + `recipe_ingredient` (ordered child) + `cook_event` (`recipe_id`, `servings_cooked`, `cooked_by`, `cooked_at`) + `tag` + `recipe_tag` (membership join), UUIDv7 PKs, `household_id` + RLS, `text`+`CHECK` enums (`tag_category`), `numeric(12,3)` quantities — per `DataModels/conventions.md`. `CostPerServing` and `FulfillmentResult` get **no tables** — they're computed read-side. The ports in §8 become the application-service interfaces wired in the App Services step.
