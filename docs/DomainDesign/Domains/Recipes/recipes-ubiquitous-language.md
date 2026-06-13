# Recipes — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 2 (naming decisions N1–N5 resolved)
>
> **Purpose:** The shared vocabulary for the Recipes bounded context. Every term here should appear verbatim in domain code, schema, and conversation. Feeds the Domain Model (next step). Built from [recipes-journeys.md](recipes-journeys.md) and aligned with the established `DataModels/` vocabulary.
>
> **Bounded context:** Recipes (`recipes` schema, Phase 2). References Catalog, Inventory, Pricing, Shopping, Identity **by ID only** (DM-3).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Naming Decisions (resolved ✅)

| # | Choice | Decision | Rationale |
|---|--------|----------|-----------|
| N1 | The child entity for one line of a recipe | **`Ingredient`** | Unambiguous inside the Recipes context; the `Recipe` prefix is redundant within the aggregate. |
| N2 | `IngredientStatus` value for an untracked staple | **`Untracked`** | Names the *reason* it's always satisfied; distinct from genuinely-in-stock so the UI can show a different indicator (C12). |
| N3 | The derived unit of Directions | **`Step`** | Matches "numbered steps" language and a future "cook mode step" (C13). |
| N4 | The label grouping ingredients | **`GroupHeading`** | The field already exists as `group_heading` (C6 / ADR-010); reuse it rather than introduce "Section." |
| N5 | Hard-negative stance value | **`Restricted`** | Clearer than "Excluded"/"Avoided," which read as soft negatives. Stance scale: Required · Preferred · Neutral · Disliked · Restricted. |

---

## Aggregates & Entities

| Term | Kind | Definition |
|------|------|------------|
| **Recipe** | Aggregate root | The household's canonical definition of a dish. Holds identity (**Name**, unique per household — C4), **Source**, **Tags**, **Photo**, **Cook time**, **Default servings**, **Directions**, and an **ordered collection of Ingredients**. Owns its Ingredients (composition); the ingredient list is replaced wholesale on edit (J7). Does **not** own CookEvent. |
| **Ingredient** | Entity (child of Recipe) | One required item in a recipe. Carries a soft-ref **Product** (`product_id`, **never null** — C12), **Quantity** (nullable for untracked — C12), **Unit** (nullable for untracked), optional **GroupHeading**, and an **ordinal** position. May reference a *parent product* (DM-19), resolved to a variant at cook time. |
| **CookEvent** | Aggregate root (own table — C3) | An immutable record that a recipe was cooked: **Recipe** ref, **ServingsCooked**, **CookedAt**. Append-only; the substrate for future history/frequency features. Lives in the `recipes` schema. |

---

## Value Objects (computed, not persisted)

| Term | Definition |
|------|------------|
| **FulfillmentResult** | For a Recipe at a given servings count: an overall fulfillment measure plus a per-Ingredient **IngredientStatus**. **Always fresh** — computed at query time from live `ProductStock`, never cached on the Recipe (cross-cutting note). Untracked ingredients count as satisfied; a parent-product ingredient rolls up stock across all variants (DM-19). |
| **IngredientStatus** | Per-ingredient availability: **`InStock`** / **`Low`** / **`Missing`** / **`Untracked`** (staple — always satisfied, C12). See N2. |
| **CostPerServing** | Computed from `PriceObservation` history (Pricing). **Partial** when some ingredients lack price data (those excluded); **omitted** entirely when none have price data (not shown as zero — J3). Untracked auto-created staples have no price history and so contribute nothing. |
| **ServingsScale** | The ratio `desired ÷ default_servings` applied to scale ingredient quantities (J3 step 5, J4 step 1). Client-side at view time; materialized into `ServingsCooked` at cook time. |

---

## Directions & Steps (C13)

| Term | Definition |
|------|------------|
| **Directions** | The single text field on Recipe holding the full method. Authored with no Markdown knowledge required — Enter starts the next step; an optional toolbar emits light inline formatting. |
| **Step** | A *derived* unit: one paragraph of Directions, auto-numbered at render. **Not persisted** as its own row. (Promotion to a `recipe_step` child table is deferred until per-step metadata like timers exists — C13.) See N3. |
| **Section heading** | A `#` line within Directions that **resets step numbering** and labels a group (e.g. "For the sauce"). |

---

## Tags

Tags exist **primarily as meal-planner inputs** matched against per-member preferences — browse-filtering (J2) is a secondary benefit (C2). This makes them a **controlled, shared vocabulary referenced by ID** (not free-text strings), so a recipe's tag and a user's preference resolve to the *same* token.

| Term | Definition |
|------|------------|
| **Tag** | A household-scoped vocabulary entry, referenced by ID. **Kind-less** — a tag carries no hard/soft semantics (see below); it's just a label like "Poultry" or "Vegan". Eight defaults seeded at household creation (C2). New tags can be created inline while authoring (J6); creation mints a shared `Tag`, not a string. |
| **Tag category** | Optional, **cosmetic** grouping on a Tag (Diet / Protein / Flavor / Cuisine) — purely to organize the preference-setting UI for households with many tags. **No planner meaning.** |
| **Recipe ↔ Tag** | Plain membership: a recipe *has* a tag (it contains poultry → tagged Poultry). No strength, no polarity — those live only on the user side. |

**Stance lives on the User↔Tag edge, not the Tag.** The hard-vs-soft force of a tag is a property of *who is looking at it* — the same "Poultry" is a mild dislike for one member and an absolute refusal for another. That strength + polarity is modelled as a **Stance** on a per-user **UserPreference** (a Meal Planning / Phase-2 concept — see cross-context table and FUTURE.md), on the scale **Required · Preferred · Neutral · Disliked · Restricted**. The Tag entity is built kind-less *now* so this needs no migration later.

---

## Domain Events

| Event | Payload | Emitted when |
|-------|---------|--------------|
| **RecipeCreated** | `recipeId, householdId, at` | A new recipe is persisted (J6). |
| **RecipeUpdated** | `recipeId, householdId, at` | An existing recipe is saved (J7). |
| **RecipeCooked** | `recipeId, householdId, servingsCooked, cookedBy, at` | A cook is confirmed (J4); pairs with the written CookEvent and the Inventory consumes. `cookedBy` mirrors `CookEvent.CookedBy` (O2). |

---

## Key Actions (verbs)

| Verb | Meaning |
|------|---------|
| **Browse** | List household recipes with live FulfillmentResult + CostPerServing; sort/filter/search (J1, J2). |
| **Inspect** | Open a recipe's detail; recompute fulfillment and cost fresh (J3). |
| **Scale** | Apply a ServingsScale to ingredient quantities (J3, J4). |
| **Cook** | Confirm consumption: resolve variants, consume tracked ingredients via Inventory, write a CookEvent, emit RecipeCooked (J4). |
| **Fulfill** | Compute a FulfillmentResult against current pantry state (J1, J3). |
| **Add missing to shopping list** | Hand the `Missing` ingredients (scaled) to Shopping (J5). |

---

## Cook-time disambiguation

| Term | Definition |
|------|------------|
| **Variant Disambiguation Picker** | The cook-time control for a parent-product Ingredient (DM-19): shows **all** variants; unit-compatible ones selectable (auto-selecting the best by stock / FEFO), unit-incompatible ones **visible but disabled** with a label (C11 — *amends DM-19, which said "excluded"*). User may split the required quantity across compatible variants. |

---

## Cross-context terms (owned elsewhere, referenced by ID)

These are **not** redefined here — this fixes which word Recipes uses for each.

| Term | Owning context | Note |
|------|----------------|------|
| **Product** / **parent product** / **variant** | Catalog (DM-10, DM-19) | An Ingredient's `product_id` is a soft-ref. |
| **Untracked staple** / `track_stock` | Catalog (C12) | A product with `track_stock = false`; auto-created inline from a name. |
| **Unit** / **dimension** / `factor_to_base` | Catalog (DM-8) | |
| **ProductConversion** | Catalog (DM-12) | Cross-dimension/density; may be authored inline from the recipe editor (C10). |
| **ProductStock** / **stock_entry (lot)** / **FEFO** | Inventory (DM-13) | Fulfillment reads it; never written by Recipes directly. |
| **Consume** | Inventory primitive (ADR-011) | Cook calls `ProductStock.Consume(qty, unit, reason, sourceRef=cookEventId)`. |
| **PriceObservation** | Pricing (DM-17) | Feeds CostPerServing. |
| **ShoppingList** / **shopping_list_item** | Shopping (DM-18) | Target of "add missing." |
| **Household** / **User** | Identity (DM-6) | Tenancy + attribution. |
| **UserPreference** / **Stance** | Meal Planning (Phase 2, not yet modeled) | Per-member stance (Required/Preferred/Neutral/Disliked/Restricted) over a `tag_id`. Consumes the Tag vocabulary; the planner reads it. See FUTURE.md. |

---

## Reconciliation note

**DM-19 vs C11.** DM-19's last sentence says cook-time disambiguation "presents only variants whose unit matches … (unit-incompatible variants are excluded, not substituted)." Decision **C11** supersedes this: incompatible variants are **shown but disabled**, not excluded. DM-19 should be amended when the domain model is written.
