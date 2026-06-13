# Plantry — Data Model Index

> Phase 1 contexts, plus Recipes (Phase 2, in design). Meal Planning and Deals (Phase 2/3) are out of scope.
> Authority: ADRs hold decision *rationale*; these files hold the *shape*.

## Status legend

| Mark | Meaning |
|---|---|
| ✅ | Decided and confirmed |
| 🔲 | Proposed, pending confirmation |
| ⏳ | Not yet modeled |

## Files

| File | Context / Topic | Status |
|---|---|---|
| [conventions.md](conventions.md) | Cross-cutting conventions (PKs, tenancy, timestamps, money, enums…) | ✅ |
| [identity.md](identity.md) | Identity & Access (`identity` schema) | ✅ |
| [catalog.md](catalog.md) | Catalog (`catalog` schema) | ✅ |
| [inventory.md](inventory.md) | Inventory (`inventory` schema) | ✅ |
| [intake.md](intake.md) | Intake (`intake` schema) | ✅ |
| [cross-cutting-behaviour.md](cross-cutting-behaviour.md) | Expiry materialization · Unit conversion · Consumption primitive | ✅ |
| [pricing.md](pricing.md) | Pricing (`pricing` schema) | ✅ |
| [shopping.md](shopping.md) | Shopping (`shopping` schema) | ✅ |
| [recipes.md](recipes.md) | Recipes (`recipes` schema, Phase 2) | 🔲 |

## Decision log

| # | Decision | Status |
|---|---|---|
| DM-1 | UUIDv7 single-column PKs; `household_id` column + RLS for tenancy; selective `UNIQUE (household_id, id)` for tenant-safe child FKs | ✅ |
| DM-2 | One PostgreSQL schema per bounded context | ✅ |
| DM-3 | No enforced cross-context FKs; hard FKs only within a context | ✅ |
| DM-4 | Soft-delete for Catalog reference data; journal & price observations append-only | ✅ |
| DM-5 | Enums as `text` + `CHECK`; money `numeric(12,2)`, quantity `numeric(12,3)`, single currency | ✅ |
| DM-6 | Auth, membership, sessions delegated to ASP.NET Core Identity; `IdentityUser<Guid>` extended with `HouseholdId` + `DisplayName`; flat membership, no join table | ✅ |
| DM-7 | `household_settings` 1:1 table; per-household AI key stored encrypted at rest, never sent to client | ✅ |
| DM-8 | `UniversalConversion` aggregate deleted; within-dimension conversion via `unit.factor_to_base` | ✅ |
| DM-9 | Reference data (units/categories/locations) seeded per-household | ✅ |
| DM-10 | Catalog: `product` rich root with SKU + product-conversion children; four expiry defaults on product | ✅ |
| DM-11 | Expiry materialized on `stock_entry` at event time via product rules; resolution chain product → category → blank | ✅ |
| DM-12 | Unit conversion resolution: same-unit → same-dimension → product-conversion, else fail loudly | ✅ |
| DM-13 | Inventory: `product_stock` root + `stock_entry` lots + immutable `stock_journal_entry`; concurrency anchor via `xmin` + `FOR UPDATE`, transfer/open as lot-state, FEFO nulls-last with `entry_id` tiebreaker, depleted-lot retention | ✅ |
| DM-14 | Journal is source of truth for *quantity* movement only; quantity-neutral lot-state transitions are current state on `stock_entry`, full move-history deferred to `stock_entry_event` (amends ADR-011 wording) | ✅ |
| DM-15 | Intake: `import_session` root + `import_line` children + 1:1 `import_receipt` source blob. ACL split — raw AI proposal quarantined in `raw_parse` jsonb + `suggested_confidence`, only user-resolved typed fields commit; commit is resumable per-line orchestration to Catalog/Inventory/Pricing recording what it wrote. Refines ADR-010 "parsed rows (jsonb)" → child table + per-row raw jsonb | ✅ |
| DM-16 | Merchant identity lives in **Catalog** (`store` reference aggregate), not Deals/Pricing; Deals' flyer-config references it by ID. **Finalized (Pricing modeled):** `price_observation` carries **both** free-text `merchant_text` (populated Phase 1) and a nullable `store_id` soft-ref (Phase 3+); the `store` table is deferred to Phase 3 with Deals. Amends ADR-010 Store placement | ✅ |
| DM-17 | Pricing: `price_observation` append-only flat root keyed `product_id` (+ nullable `sku_id`); `source` `purchase`\|`deal`; `unit_price` materialized at write, failing **soft** (null) on cross-dimension — unlike `Consume`; deal validity window (`valid_from`/`valid_to`) lives on the row so the "cheapest active deal" read model stays within-context; no children, no composite FK | ✅ |
| DM-18 | Shopping: `shopping_list` root + `shopping_list_item` children; **mutable working state** (edit-in-place, hard-delete on clear — not append-only, not soft-delete); one list/household in v1 (extensible); item is exactly one of `product_id`\|`free_text` (`num_nonnulls = 1`); `checked_at` timestamp (not boolean) + `checked_by`; `source`/`source_ref` provenance; duplicate-product merge in app layer, no DB constraint; category grouping + deal badge are read-time joins; check-off does **not** write stock | ✅ |
| DM-19 | Product groups: `product.parent_product_id` nullable self-ref FK. A product with no parent and at least one variant child is abstract (no stock). Variants carry their own `default_unit_id`. Max depth = 1 enforced in app layer. `StockEntry` and `StockJournalEntry` always reference a non-parent product. Fulfillment rollup sums across all variants of a parent; cook-time disambiguation presents **all** variants, with unit-incompatible ones shown but disabled (labelled, not selectable) rather than hidden (amended by recipes C11 — supersedes the original "excluded, not substituted" stance). | ✅ |
| DM-20 | Recipes (Phase 2): `recipe` root + ordered `recipe_ingredient` child + 1:1 `recipe_photo` (bytea off the hot row, ADR-009) + append-only `cook_event` (`recipe_id`, `servings_cooked`, `cooked_by`, `cooked_at`) + `tag` root + `recipe_tag` membership join. Recipe **soft-deleted** (`archived_at`) so append-only cook history stays FK-valid; ingredients **wholesale-replaced** with re-minted IDs on save (CASCADE); `directions` single `text` column (steps derived at render, no `recipe_step` table); `tag` kind-less, `category` `text`+`CHECK` (no Stance — future Meal-Planning `UserPreference`). `FulfillmentResult`/`CostPerServing` are **computed read-side, no tables**. Browse Fulfillment/Cost sorts are cross-context computed (no local index); Name/Cook-time/Recently-added sort on local indexes. | 🔲 |
