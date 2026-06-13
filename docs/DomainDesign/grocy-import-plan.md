# Grocy → Plantry Import Plan

**Status:** Proposal · **Author:** autonomous analysis · **Date:** 2026-06-13
**Scope (as briefed):** Products + dependent reference data (Units, Conversions, Locations, Product Groups) and Recipes.
**Out of scope (decided):** Stock/lots, Meal Plan, Shopping List, Pricing/price history, Chores/Batteries/Tasks/Equipment.

> Source instance probed live via the Grocy REST API (`/api/objects/*`, `/api/system/info`).
> Grocy **v4.5.0** (PHP 8.3, SQLite). Credentials read from `GROCY_URL` / `GROCY_KEY` (User-scope env); never printed.

---

## 1. Executive summary

A **full-fidelity** import is achievable for the *structural* core — units, conversions, locations,
categories, products (incl. the parent/variant tree), recipes, and recipe ingredients — because every
Plantry NOT-NULL / invariant constraint is satisfiable from the source data (verified, §3). Fidelity is
**lost in five places**, each a deliberate Plantry simplification rather than a bug; all are logged in §8:

1. **Grocy's per-product 4-way unit roles** (purchase / stock / consume / price) collapse to Plantry's single `default_unit_id`.
2. **Grocy units carry no dimension or base-factor** — Plantry's whole conversion model is dimension + `factor_to_base`. Dimensions must be *assigned* and factors *derived* from the global conversion graph (the central reconciliation task).
3. **Recipe nestings** (recipe-includes-sub-recipe, 16 edges) have no Plantry equivalent.
4. **Product barcodes, calories, min-stock, tare-weight, "produces product" links, recipe ingredient notes** have no Plantry catalog/recipe home.
5. **Shopping locations** (stores) belong to the not-yet-built Pricing context.

The work is a **staged migration pipeline modeled on the existing Intake flow** (extract → stage → reconcile → commit), not a raw SQL load. Recipe import is **gated on the Recipes context being implemented** (currently design-only in `DomainDesign/Domains/Recipes/`).

---

## 2. Architecture decision — where it lives and why

**Decision: an in-process, admin-only staged import inside `Plantry.Web`, backed by a new `Plantry.Migration.Grocy` library, committing *through the existing Catalog/Recipes application services* — never raw SQL.**

Rationale, tied to project conventions (`.claude/CLAUDE.md`, ADRs):

- **Reuse the Intake "AI/untrusted-input staging" pattern.** Plantry already has the exact shape this needs: pull external data → stage it → let the user reconcile on an htmx Review screen → commit. The Grocy importer is "Intake for a whole database." Reusing that pattern (staging tables + review fragments + a commit service) keeps it idiomatic.
- **Commit through application services** (`ProductCommands`, `UnitCommands`, `CategoryCommands`, `LocationCommands`, and the future Recipes `AuthorRecipe`) so every invariant holds automatically: max-depth-1 variants, `factor_to_base` base-unit rule, RLS/`household_id` scoping, `UNIQUE(household_id, …)` collisions, expiry-days non-negative. A raw load would have to re-implement all of these and would bypass RLS.
- **Hypermedia UI, no SPA** (per CLAUDE.md): the reconcile screens are Razor + htmx fragments, mirroring `Pages/Intake/_ReviewRow.cshtml`.
- **In-process, not a separate service:** one-time, single-household, single-tenant migration. A microservice or queue is over-engineering.

### 2.1 Pipeline stages

```
┌─────────────┐   ┌──────────────────┐   ┌─────────────────────┐   ┌──────────────┐
│ 1. EXTRACT  │ → │ 2. STAGE         │ → │ 3. RECONCILE (htmx) │ → │ 4. COMMIT    │
│ Grocy REST  │   │ normalized       │   │ map units, groups,  │   │ via Catalog/ │
│ → manifest  │   │ "migration       │   │ locations; review   │   │ Recipes app  │
│ (JSON)      │   │  manifest"       │   │ product/recipe diff │   │ services     │
└─────────────┘   └──────────────────┘   └─────────────────────┘   └──────────────┘
```

- **Extract** — A `GrocyClient` (typed `HttpClient`) reads the in-scope `/api/objects/*` collections into an immutable **manifest** (a versioned JSON snapshot). Decoupling extraction means the rest of the pipeline never depends on Grocy being online, and the manifest is a re-runnable, diffable artifact. Source credentials come from config/user-secrets (same pattern as `AI:ApiKey`, see beads memory), not hard-coded.
- **Stage** — manifest rows are loaded into per-entity staging records with a resolved-mapping column and a status (`Auto`, `NeedsReview`, `Mapped`, `Skipped`). No domain writes yet.
- **Reconcile** — the user-facing step the brief calls for (§6). Finite reference sets (25 units, 8 groups, 5 locations) get an explicit mapping grid; the 215 products / 65 recipes get a review list that only *demands* attention on exceptions.
- **Commit** — ordered, idempotent writes (§7) through the app services, inside a transaction per entity batch, keyed for safe re-runs.

**Alternative considered & rejected:** a standalone `tools/` console importer doing EF `AddRange`. Rejected — it would bypass the application-layer invariants and RLS, and the brief explicitly wants an interactive reconcile/map surface, which belongs in the hypermedia UI. (A thin **headless mode** that runs Extract+auto-map+Commit with the default mappings is a cheap add-on for a no-UI dry run, but the UI path is primary.)

---

## 3. Source data inventory (live counts)

| Grocy object | Rows | In-scope notes |
|---|---:|---|
| `products` | 215 | 32 variants (parent_product_id set); **all variant chains are depth-1** (0 depth>1) → maps cleanly to Plantry max-depth-1 ✅ |
| `quantity_units` | 25 | no dimension, no base factor (see §4.1) |
| `quantity_unit_conversions` | 188 | 22 **global** (→ derive `factor_to_base`), 166 **product-specific** (→ `product_conversion` or dropped, §4.2) |
| `locations` | 5 | `is_freezer` flag present |
| `product_groups` | 8 | → `category`; names differ from Plantry seeds |
| `shopping_locations` | 3 | Costco / Superstore / Metro — **no catalog home** (§4.6) |
| `product_barcodes` | 33 | pure UPC strings; **amount always 1** (no pack-size payload) |
| `recipes` | 415 | **only 65 are `type=normal`**; rest are meal-plan artifacts (`mealplan-day` 172, `mealplan-shadow` 139, `mealplan-week` 39) → **out of scope** |
| `recipes_pos` (ingredients) | 1949 total / **399 on normal recipes** | every normal-recipe row has product_id, qu_id, **and** amount (0 nulls) → Plantry NOT-NULLs satisfiable ✅ |
| `recipes_nestings` | 5457 total / **16 on normal recipes** | recipe-includes-recipe; **no Plantry equivalent** (§5.3) |
| `userfields` | 1 | `recipes.original_recipe` (type `link`) → recipe `source` (§5.1) |

Unused Grocy product features confirmed empty (→ safe to drop, no fidelity loss): `no_own_stock`=0 for all, `calories`=0 for all, tare-weight handling off for all, `due_type`=1 (best-before) uniformly.

---

## 4. Catalog mapping

### 4.1 Units — the central reconciliation task

**The core mismatch:** Plantry units = `dimension ∈ {mass,volume,count}` + `factor_to_base` (within-dimension conversion is pure linear scaling; there is **no pairwise table** — `catalog.md` simplification #1). Grocy units have **neither** — all conversion is pairwise rows. So the importer must (a) **assign a dimension** to each Grocy unit and (b) **derive `factor_to_base`** by walking Grocy's global conversion graph to a base unit.

Plantry already **seeds** units per household (`CatalogReferenceDataSeeder`): `g, kg, mg, oz` (mass); `ml, l, fl oz, cup, tsp, tbsp` (volume); `ea, pk, doz` (count). So most Grocy units **match an existing seeded unit**; the rest are **created**.

| Grocy unit (id) | Dimension | → Plantry unit | factor_to_base | Disposition |
|---|---|---|---|---|
| Gram (13) | mass | `g` (base) | 1 | match seed |
| Kg (18) | mass | `kg` | 1000 | match seed |
| oz (12) | mass | `oz` | 28.3495* | match seed (*Grocy stored 28.35; Plantry seed wins, §8-T7) |
| ml (15) | volume | `ml` (base) | 1 | match seed |
| Liter (17) | volume | `l` | 1000 | match seed |
| Cup (9) | volume | `cup` | 240* | match seed (*Grocy stored 237; +1.3% drift, §8-T7) |
| tsp (10) | volume | `tsp` | 4.92892* | match seed (*Grocy stored 14.7867 — anomalous, §8-T8) |
| tbsp (11) | volume | `tbsp` | 14.7868* | match seed (*Grocy stored 17.7581 — anomalous, §8-T8) |
| Pint (24) | volume | **create** `pt` | 474 (2×cup) | new unit |
| Quart (25) | volume | **create** `qt` | 948 (4×cup) | new unit |
| 1/2 Cup (26) | volume | *drop → use `cup`×0.5* | — | redundant fraction (§8-T9) |
| 1/4 Cup (27) | volume | *drop → use `cup`×0.25* | — | redundant fraction (§8-T9) |
| Piece (2) | count | `ea` (base) | 1 | match seed |
| Pack (3) | count | `pk` | 1 | match seed |
| Case12 (6) | count | **create** `case12` | 12 | new (dozen-like) |
| Case24 (21) | count | **create** `case24` | 24 | new |
| Can (4), Jar (7), Bottle (8), Head (5), Clove (14), Bulb (22), Bunch (23), Portion (20), Recipe (28) | count | **create** each | 1 | discrete count units; no within-dimension scaling |

**Dimension assignment algorithm (auto, user-confirmable):**
1. Seed-match by name/synonym first (Gram→g, Kg→kg, Liter→l, Piece→ea …) — inherits the seed's dimension + factor.
2. For the rest, build the **global conversion graph** (22 edges). Find connected components. A component containing a known mass unit (Gram) ⇒ mass; containing ml ⇒ volume; otherwise ⇒ count. `factor_to_base` = product of edge factors along the path to the component's base unit.
3. Units in **no** global conversion (Can, Jar, Bottle, Head, Clove, Bulb, Bunch, Portion, Recipe) ⇒ **count, factor 1** (Plantry treats discrete packaging as count units; this is exactly how `ea`/`pk` are seeded).
4. Present every assignment on the **Unit Mapping grid** (§6) for confirm/override. This is the one screen the user should expect to actually think on.

**Decision — "Recipe" unit (28):** Grocy's `Recipe` quantity unit (used by produces-product recipes) → import as a **count** unit `recipe`, factor 1. It only appears on the 21 produces-product links we are dropping (§5.2) and on no in-scope ingredient, so practically inert; create it for completeness, flag low-value.

### 4.2 Quantity-unit conversions

- **Global conversions (22):** consumed by the §4.1 derivation; they become `factor_to_base` values, **not** rows. Within-dimension pairs (Kg↔Gram, Cup↔ml, …) are fully reconstructable from two unit rows — Plantry has no pairwise table by design.
- **Product-specific conversions (166):** these are Grocy's per-product overrides — overwhelmingly **cross-dimension density** (e.g. `1 Piece onion = 110 Gram`, `1 Bunch = 340 Gram`, `1 Cup chopped = 125 Gram`). These map **directly** to Plantry `product_conversion` (`from_unit → to_unit`, `factor`), which is *defined* as "cross-dimension / density only" (`catalog.md`). Classification on import:
  - `from`/`to` resolve to **different dimensions** ⇒ `product_conversion` row (full fidelity).
  - `from`/`to` in the **same dimension** ⇒ already expressible via `factor_to_base`; **drop as redundant** (log count). A same-dimension product override that *disagrees* with the universal factor (a product-specific "this flour cup is 130 g not the universal 120") is **kept** as a `product_conversion` (it is genuinely product-dependent) and flagged.
  - Conversions whose product was dropped/merged ⇒ dropped with their product.

### 4.3 Locations

Clean, full fidelity. `locations.is_freezer = 1` ⇒ `LocationType.Frozen`, else `Ambient`. Name-match to the four seeded Plantry locations (Fridge, Freezer, Pantry, Counter); create any extras (Grocy has 5). `description` has no Plantry field → dropped (cosmetic, §8-T10).

### 4.4 Product groups → categories

8 Grocy groups → Plantry `category`. Names differ, so **reconcile by fuzzy name match, user-confirmable**:

| Grocy group | → Plantry category (seeded) |
|---|---|
| Fruit & Veg | Fruits and Vegetables |
| Frozen Food | Frozen |
| Meat | Meat & Fish |
| Drinks | Drinks |
| Condiments | Condiments |
| Herbs | Herbs and Spices |
| Spices | Herbs and Spices *(two Grocy groups → one Plantry category; or create "Spices")* |
| Prepared (Homemade) | **create** new category |

Grocy groups carry **no default-expiry**; Plantry categories have `default_due_days`. New/created categories get `null` (no category-level fallback) unless the user sets one on the mapping grid. The Herbs+Spices collapse and the Prepared category are the only judgement calls — surfaced on the Category Mapping grid (§6).

### 4.5 Products (the aggregate root)

| Grocy field | → Plantry `product` | Fidelity |
|---|---|---|
| `name` | `name` | ✅ (collision-check `UNIQUE(household_id,name)`; suffix dupes) |
| `product_group_id` | `category_id` (via §4.4 map) | ✅ |
| `parent_product_id` | `parent_product_id` + set parent's `HasVariants` | ✅ (all depth-1) |
| `qu_id_stock` | `default_unit_id` | ⚠️ **chosen** of 4 roles (§8-T1) |
| `location_id` | `default_location_id` | ✅ |
| `default_best_before_days` | `default_due_days` | ⚠️ sentinel remap (below) |
| `default_best_before_days_after_open` | `default_due_days_after_opening` | ⚠️ sentinel remap |
| `default_best_before_days_after_freezing` | `default_due_days_after_freezing` | ⚠️ sentinel remap |
| `default_best_before_days_after_thawing` | `default_due_days_after_thawing` | ⚠️ sentinel remap |
| (no Grocy equivalent) | `track_stock` | default **true** (§8-T2) |
| `qu_id_purchase` / `qu_id_consume` / `qu_id_price` | — | **dropped** (§8-T1); relationship preserved via `product_conversion` where one existed |
| pack size (from purchase `qu` conversion) | optional `product_sku` | partial (§4.5.1) |
| `min_stock_amount` | — | **dropped** (no min-stock concept; §8-T3) |
| `calories` | — | dropped (all 0 — zero loss) |
| `shopping_location_id` | — | dropped (Pricing; §4.6) |
| `picture_file_name` | — | **dropped** (no product image in catalog; §8-T4) |
| `enable_tare_weight_handling`/`tare_weight` | — | dropped (all off — zero loss) |
| `not_check_stock_fulfillment_for_recipes` | — | dropped (§8-T5) |
| `row_created_timestamp` | `created_at` | ✅ preserved |

**Expiry sentinel remap (decided):** Grocy uses `-1` = "never expires" and `0` = "no default." Plantry's `SetExpiryDefaults` **rejects negatives**. So `-1 → null` and `0 → null` (both mean "no stored default" to Plantry; 30 products are `-1`, 75 are `0`). Positive values pass through. This is lossless in practice — "never expires" is represented by the absence of a due-days default, which is how Plantry models a non-expiring staple.

**Variant inheritance:** after attaching variants, call `Product.InheritFrom(parent)` so the depth-1 groups get Plantry's expiry/conversion inheritance handshake — matching native Plantry behaviour rather than leaving variants bare.

#### 4.5.1 Barcodes & pack sizes → SKUs

33 products have barcodes, but **every barcode's `amount` is 1** — they are pure UPC strings with no pack-size payload, and Plantry catalog has **no barcode field**. **Decision: drop barcodes** (§8-T6). Where a product's `qu_id_purchase` differs from `qu_id_stock` with a defined conversion (a real "buys in cans, stocks in ml" pack), synthesize **one `product_sku`** capturing `label` = purchase unit name + `size_quantity`/`size_unit` from the conversion — recovering the pack-size half of the multi-unit fidelity loss for the 14 multi-unit products.

### 4.6 Shopping locations (stores)

Costco / Superstore / Metro. **No Catalog home** — stores live in the **Pricing** context (price observations carry a store), which is **out of scope and not yet built**. **Decision: extract and park them in the manifest** (so a later Pricing import can consume them) but **do not commit** anything now (§8-T11).

---

## 5. Recipes mapping  *(gated on Recipes context implementation)*

> Recipes is **design-only** today (`DomainDesign/Domains/Recipes/`, `DataModels/recipes.md`). This section maps against the *designed* schema; the recipe import phase **cannot start until the Recipes context (aggregate, app services, EF mappings, migrations) exists**. Until then, recipes are extracted to the manifest only.

### 5.1 Recipe (root) — 65 normal recipes

| Grocy `recipes` field | → Plantry `recipe` | Fidelity |
|---|---|---|
| `name` | `name` | ✅ (`UNIQUE(household_id,name)`; suffix dupes) |
| `description` (HTML) | `directions` (text) | ⚠️ HTML→text conversion (below) |
| `base_servings` | `default_servings` (`CHECK >= 1`) | ✅ |
| `desired_servings` | — | not stored (Plantry `ServingsScale` is a transient view value) — zero loss |
| `userfields.original_recipe` (URL) | `source` | ✅ |
| `picture_file_name` (16 have one) | `recipe_photo` (1:1, bytea) | ✅ if we fetch bytes (below) |
| `product_id` (21 produce-a-product) | — | **dropped** (§5.2 / §8-T12) |
| `not_check_shoppinglist`, `type` | — | dropped (meal-plan/shopping concern) |
| `row_created_timestamp` | `created_at` | ✅ |

**Directions HTML→text (decided):** Grocy stores `<p>…</p>` paragraphs (verified). Plantry `directions` is a single text field where **paragraphs = derived steps** and a leading `#` line = a section reset (`recipes.md` Resolved-call 4). Convert: each `<p>` → one paragraph (blank-line separated); strip remaining tags; decode entities; lists (`<li>`) → lines. This lands natively in Plantry's step-derivation model. Inline formatting (bold/links inside directions) is flattened to text (§8-T13).

**Photos (decided):** fetch the 16 images via `GET /api/files/recipepictures/{name}` during Extract, store bytes + content-type into `recipe_photo`. Full fidelity. (If the Recipes photo endpoint/table isn't built when the recipe phase runs, photos defer to a follow-up — recipe text is independent of them.)

**Tags:** Grocy core recipes have **no tag concept** (verified: no tag table/field in scope). Plantry's rich tag system therefore imports **empty** — the 8 seeded default tags exist but nothing is auto-applied. Optional enhancement: derive tags from group names/ingredients later; **not** part of full-fidelity import (there is no source data to be faithful to).

### 5.2 "Produces product" recipes (21)

Grocy lets a recipe declare it *produces* a product (batch cooking → stock). Plantry recipes have **no produce-a-product link**. **Decision: drop the link**, but **append a provenance line to `source`** (e.g. `… · produces: <product name>`) so the human-meaningful fact survives as text (§8-T12). The recipe itself imports fully; only the inventory-producing semantics are lost (and inventory is out of scope anyway).

### 5.3 Recipe nestings (16 edges on normal recipes)

Grocy lets a recipe **include another recipe** as a component. Plantry's `recipe_ingredient` references **products only** (`product_id NOT NULL`) — there is **no recipe-as-ingredient**. This is the one genuinely structural recipe gap. Options weighed:

- **(A) Drop** the nesting → lose the composition.
- **(B) Flatten** → inline the sub-recipe's ingredients into the parent (multiply by `servings`). Distorts authoring intent and can double-count.
- **(C) Reference as text** → add a `group_heading` + a note-style line naming the included recipe.

**Decision: (C) — preserve as a labelled section.** For each nesting, emit a `group_heading` like *"Includes: <sub-recipe name>"* on the parent recipe and, if the sub-recipe is itself imported, leave the human a clear pointer in `directions`. Rationale: keeps the parent recipe coherent and honest (no fabricated/double-counted quantities), survives as meaningful text, and is reversible if Plantry ever adds sub-recipes. 16 edges → low volume, easy to review. Logged §8-T14.

### 5.4 Recipe ingredients — 399 rows on normal recipes

| Grocy `recipes_pos` field | → Plantry `recipe_ingredient` | Fidelity |
|---|---|---|
| `product_id` | `product_id` (NOT NULL) | ✅ all present |
| `amount` | `quantity` | ✅ all present, non-zero |
| `qu_id` | `unit_id` (via §4.1 map) | ✅ all present |
| `ingredient_group` (44 rows) | `group_heading` | ✅ (labels: Dough, Filling, Salad, Vinaigrette, Stir Fry Sauce, Rice, Dressing, Gremolata, …) |
| row order | `ordinal` (contiguous, re-minted) | ✅ |
| `note` (8 rows) | — | **dropped** — Plantry ingredient has no note field (§8-T15) |
| `variable_amount` | — | unused (0 rows) — zero loss |
| `not_check_stock_fulfillment` (4), `only_check_single_unit_in_stock`, `price_factor`, `round_up` | — | dropped (fulfillment/pricing tuning, no Plantry analog) |

**Untracked-staple nuance:** Plantry allows `quantity`/`unit` null *only* for an untracked staple ("to taste"). Grocy always has amount+unit, so every imported ingredient is fully quantified — no null-handling needed. The 8 dropped notes are the only ingredient-level loss; surface them in the recipe review list so the user can fold any critical note into `directions` manually.

---

## 6. Reconciliation / mapping UX (the user-facing step)

Three **mandatory mapping grids** (small, finite, htmx) + two **review lists** (bulk, exception-driven).

**Mapping grids** — each row = a Grocy entity, a suggested Plantry target (pre-filled by auto-match), and a control to confirm / re-point / create-new:

1. **Unit Mapping** (25 rows) — the one that needs real attention. Columns: Grocy unit · assigned dimension · target Plantry unit (match-existing / create-new) · `factor_to_base` (editable). Anomalies pre-flagged: the **tsp/tbsp factor anomaly** (Grocy's stored tsp=14.79 ≈ a tablespoon) and the **Cup 237-vs-240** drift. Recommended default = *match seeded unit, Plantry factor wins* — preserves a clean unit list; the user can instead choose "create distinct unit, keep Grocy factor" to preserve exact recipe math.
2. **Category Mapping** (8 rows) — fuzzy-matched; the Herbs/Spices collapse and "Prepared (Homemade)" are the only decisions.
3. **Location Mapping** (5 rows) — near-automatic (freezer flag).

**Review lists** — paged htmx tables, reusing the Intake `_ReviewRow` pattern; **green by default, flag only exceptions**:

4. **Product Review** (215) — flags: name collisions, multi-unit products (shows which unit was chosen + the synthesized SKU), category/unit set to "create-new", dropped barcodes. Inline fix without leaving the page.
5. **Recipe Review** (65, recipe phase) — flags: HTML that converted poorly, dropped ingredient notes (8), produces-product links (21), nestings (16). Per-recipe "looks good / edit" toggle.

**Pre-commit summary**: counts of create-new vs match, total rows per entity, and the full §8 tradeoff log rendered inline so the user accepts losses explicitly before commit.

---

## 7. Commit order, idempotency, re-runs

**Dependency order (must commit in this sequence):**
```
Units → Categories → Locations → Products (parents before variants) → Product conversions → Product SKUs
   → [Recipes phase:] Recipes → Recipe ingredients → Recipe photos → (nesting section-headings)
```
- **Parents before variants** so `MakeVariantOf` + parent `HasVariants` resolve; conversions after products; ingredients after both products *and* units exist.
- **Idempotency:** every staged row carries its **Grocy source id**; the commit records a `grocy_id → plantry_id` crosswalk (a migration-only table or manifest sidecar). Re-running **upserts** by source id rather than duplicating — so a partial/failed run is resumable and the import can be run twice safely.
- **Transactions:** one transaction per entity batch (not one giant transaction) so a failure in, say, recipes doesn't roll back a good product import. The crosswalk makes the boundary safe.
- **Dry-run mode:** Extract + auto-map + render the pre-commit summary with **zero writes** — lets the user see every decision and tradeoff before committing.

---

## 8. Tradeoff log (consolidated — every fidelity loss, with the decision)

| # | Loss | Decision & rationale | Severity |
|---|---|---|---|
| **T1** | Grocy's 4 per-product unit roles (purchase/stock/consume/price) → 1 `default_unit_id` | Use **`qu_id_stock`** (Plantry's `default_unit_id` *is* the tracking/stock unit). Purchase pack recovered as a `product_sku` (§4.5.1); purchase↔stock relationship recovered as a `product_conversion`. Price/consume unit divergence (rare) lost. | Low–Med |
| **T2** | No Grocy "track vs untracked staple" flag | Default `track_stock = true` for all. User flips staples (salt, water) post-import. | Low |
| **T3** | `min_stock_amount` | Dropped — Plantry has no min-stock / auto-reorder concept in scope. | Low |
| **T4** | Product images | Dropped — catalog has no product image (only recipes have photos). | Low |
| **T5** | `not_check_stock_fulfillment_for_recipes` | Dropped — Plantry models this via untracked staples instead. | Low |
| **T6** | Product barcodes (33) | Dropped — no catalog barcode field; all carried `amount=1` (no pack payload to salvage). | Low |
| **T7** | Unit factor drift on seed-match (Cup 237→240 ≈ +1.3%; oz 28.35→28.3495) | Match seeded unit, **Plantry factor wins** — keeps unit list clean; ≤1.3% quantity drift. User may opt to keep Grocy factors per-unit on the grid. | Low |
| **T8** | Grocy tsp/tbsp factors are **anomalous** (tsp=14.79 ≈ a tbsp) | **Flagged on the Unit grid.** Default to Plantry's correct factors (fixes latent bad data); user can preserve Grocy's exact values to keep historical recipe math identical. | Med (user choice) |
| **T9** | "1/2 Cup", "1/4 Cup" units | Collapsed to `cup × 0.5 / 0.25` — fractions aren't distinct units in Plantry. | Low |
| **T10** | Location/group/unit `description` text | Dropped — no Plantry field; cosmetic. | Low |
| **T11** | Shopping locations / stores (3) | **Parked in manifest, not committed** — they belong to the unbuilt Pricing context. | Med (deferred) |
| **T12** | Recipe "produces product" link (21) | Link dropped; fact appended to recipe `source` as text. Inventory-producing semantics lost (inventory out of scope). | Med |
| **T13** | Inline rich formatting inside recipe directions | Flattened to text (paragraphs preserved as steps; bold/links lost). | Low |
| **T14** | Recipe nestings (16) | Preserved as a labelled `group_heading` ("Includes: X") — no flatten/double-count, reversible. | Med |
| **T15** | Recipe ingredient notes (8) | Dropped — no per-ingredient note field; surfaced in Recipe Review for manual folding into directions. | Low |

No tradeoff is **High** severity: the structural core imports at full fidelity; every loss is either an unused Grocy feature (zero real data), a cosmetic field, or a deliberate Plantry simplification with a text-preserving fallback.

---

## 9. Phasing & sequencing

1. **Phase 0 — Extract + manifest** (no Plantry deps): `GrocyClient`, manifest schema, dry-run dump. Validates the §3 numbers against a frozen snapshot.
2. **Phase 1 — Catalog import** (units → conversions → locations → categories → products → SKUs) with the three mapping grids + Product Review. Fully buildable **now** against the existing Catalog context.
3. **Phase 2 — Recipes import** — **blocked on** the Recipes bounded context being implemented (aggregate + app services + EF + migrations, per `DomainDesign/Domains/Recipes/`). Recipe extraction can ship in Phase 0; the commit half waits.
4. **Phase 3 (optional, future)** — Pricing import consuming the parked stores + barcode last-prices; derived recipe tags.

**Suggested beads epic:** one epic with child issues per phase/entity (Extractor, Unit-mapping grid, Category/Location grids, Product commit, Product review, Recipe extractor, Recipe commit [blocked], crosswalk/idempotency, dry-run/summary). Per project convention, track this in `bd`, not here.

---

## 10. Open questions for the user (non-blocking — sensible defaults chosen)

1. **Unit factors:** accept the "Plantry factor wins" default (clean list, ≤1.3% drift, fixes the tsp/tbsp anomaly), or preserve Grocy's exact stored factors to keep historical recipe math byte-identical? *(Default: Plantry wins, with the anomaly flagged.)*
2. **Herbs + Spices:** collapse both Grocy groups into Plantry "Herbs and Spices", or create a distinct "Spices" category? *(Default: collapse.)*
3. **Target household:** import into a fresh household, or an existing one with its own data (changes collision handling)? *(Default: fresh household with standard seeds.)*
4. **Recipe phase:** build the Recipes context first (unblocks Phase 2), or ship Catalog import now and defer recipes? *(Default: ship Catalog now; recipes follow the context build.)*
