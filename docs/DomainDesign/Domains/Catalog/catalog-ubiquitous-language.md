# Catalog — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 1 (backfilled)
>
> **Purpose:** The shared vocabulary for the Catalog bounded context. Terms here — Product, Unit, Dimension, Category, Location — appear verbatim in schema column names, code identifiers, and in the cross-context reference language used by every other context.
>
> **Bounded context:** Catalog (`catalog` schema, Phase 1).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregates & Entities

| Term | Kind | Definition |
|---|---|---|
| **Product** | Aggregate root | A catalogued item: name, category, default unit, storage location, expiry defaults, and optional pack variants (SKUs) and density conversions. The universal reference — every Inventory lot, Pricing observation, Recipe ingredient, and Shopping item resolves to a `product_id`. |
| **ProductSku** | Entity (child of Product) | A specific pack size / variant: "2 L carton," "500 g bag." Carries size quantity + unit. Used by Pricing for per-SKU price capture. Optional — products without SKUs have a single implicit size. |
| **ProductConversion** | Entity (child of Product) | A **product-specific** cross-dimension density: "1 cup of *this product* flour = 120 g." Never universal. Used by the unit conversion engine when a recipe or consume crosses dimensions (e.g., volume → mass). |
| **Unit** | Reference aggregate | A quantity unit (g, ml, kg, ea, cup…) with a `dimension` and a `factor_to_base` multiplier. Seeded per-household. |
| **Category** | Reference aggregate | A grouping for products (Dairy, Produce, Meat…) with an optional `default_due_days` expiry fallback and a `sort_order` for the store-layout pantry view. Seeded per-household. |
| **Location** | Reference aggregate | A storage location (Fridge, Freezer, Pantry…) with a `type` of `frozen` or `ambient`. `frozen` triggers freeze/thaw expiry logic. Seeded per-household. |

---

## Key Terms

| Term | Definition |
|---|---|
| **Parent product** | A `Product` with `parent_product_id IS NULL` that has at least one variant child. Abstract — no stock entries may reference it. Fulfillment rolls up stock across all its variants. |
| **Variant** | A `Product` with `parent_product_id` set (e.g., "Whole Milk 2 L" and "Whole Milk 4 L" under "Whole Milk"). Has its own `default_unit_id` and stock. Max depth = 1. |
| **Standalone product** | A `Product` with no parent and no variant children — the common case. |
| **SKU** (stock-keeping unit) | A `ProductSku` row — a specific size/pack description associated with a product. Used by Pricing for unit-normalized price comparison. |
| **Dimension** | The physical quantity kind of a unit: `mass`, `volume`, or `count`. Within-dimension conversions are always `factor_to_base` arithmetic. Cross-dimension needs a `ProductConversion`. |
| **`factor_to_base`** | The multiplier on a `Unit` that converts 1 of that unit to the dimension's base unit (base = 1). `kg` → factor 1000 (base unit: `g`). Used for all within-dimension conversion — no pairwise table. |
| **Untracked staple** | A product with `track_stock = false` (salt, pepper, water, oil). A full Catalog citizen — can have SKUs and price history — but exempt from quantity accounting. Fulfillment: always satisfied. Cook consume: skipped. Shopping auto-add: never. |
| **Inline auto-create** | When a recipe author or intake user types a name that matches no product, the system mints an untracked-staple product from the name alone and warns the user. The user can later enable tracking. |
| **Archived** | A soft-deleted product (`archived_at IS NOT NULL`). Hidden from search and intake, but retained for stock/journal/pricing history integrity. |
| **Expiry defaults** | Four product-level columns (`default_due_days`, `after_opening`, `after_freezing`, `after_thawing`). Resolution chain: product → category → blank. Materialised onto `stock_entry.expiry_date` at lot creation. |
| **Product conversion** | A `ProductConversion` row — the specific density factor for *this* product between two cross-dimension units. Authored in the Catalog product screen, or inline from the recipe editor (C10). |

---

## Key Actions

| Verb | Meaning |
|---|---|
| **Create product** | Add a new product to the household catalog; optionally add SKUs and conversions at the same time. |
| **Archive product** | Soft-delete — sets `archived_at`; product disappears from active flows but historical data is intact. |
| **Add SKU** | Attach a new pack-size entry to an existing product. |
| **Add conversion** | Record a cross-dimension density for a product (`ProductConversion`). Can happen inline from the recipe editor. |
| **Seed** | Populate reference data (units, categories, locations) for a new household at registration. |

---

## Cross-context terms (originate here, referenced everywhere)

| Term | Used in |
|---|---|
| `product_id` | Inventory (`stock_entry`, `product_stock`), Pricing (`price_observation`), Intake (`import_line`), Shopping (`shopping_list_item`), Recipes (`recipe_ingredient`) |
| `sku_id` | Inventory (`stock_entry`), Pricing (`price_observation`), Intake (`import_line`) |
| `unit_id` | Inventory (`stock_entry`, journal), Pricing (`price_observation`), Intake (`import_line`), Recipes (`recipe_ingredient`) |
| `category_id` | Inventory / Pantry view (grouping), Shopping (category grouping read model) |
| `location_id` | Inventory (`stock_entry`), Intake (`import_line`) |
| `track_stock` | Inventory (skips untracked lots), Recipes (fulfillment + cook behaviour), Shopping (never auto-adds untracked) |
| `factor_to_base` | Unit conversion engine — called by Inventory consume, Pricing normalization, Recipes authoring |
