# Catalog — Domain Model

> **Status:** Implemented — Phase 1. Retrospective backfill; the DDD process was formalized after these contexts were designed.
>
> **Purpose:** The universal upstream reference supplier. Owns every physical-goods concept: products, their pack-size variants (SKUs), density conversions, units, categories, and storage locations. Every other context references Catalog entities **by ID only** — nothing modifies Catalog data except Catalog's own application services (and inline creation paths via ports).
>
> **Bounded context:** Catalog (`catalog` schema, Phase 1). Referenced by Inventory, Intake, Pricing, Shopping, and Recipes; reads nothing from them.
>
> **Code shape:** `Product` is the rich aggregate root with `ProductSku` and `ProductConversion` as owned children. `Unit`, `Category`, and `Location` are reference aggregates seeded per-household.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregate map

| Aggregate root | Identity | Owns (composition) | Lifecycle |
|---|---|---|---|
| **Product** | `ProductId` | `ProductSku[]` (optional), `ProductConversion[]` | Soft-deleted (`archived_at`); never hard-deleted (stock history refs survive) |
| **Unit** | `UnitId` | — | Reference data; seeded per-household at registration; rarely modified |
| **Category** | `CategoryId` | — | Reference data; seeded per-household |
| **Location** | `LocationId` | — | Reference data; seeded per-household |

`ProductSku` (a pack-size variant like "2 L carton") and `ProductConversion` (a product-specific density) are owned child entities of `Product` — they have no independent lifecycle.

### Product groups (DM-19)

A `Product` may be a **parent product** (abstract; has variant children, no stock) or a **variant** (`parent_product_id` set; has its own `default_unit_id` and stock). Max depth = 1, enforced in the app layer. `StockEntry` and `PriceObservation` always reference a non-parent product.

---

## Invariants

| # | Invariant | Enforced |
|---|---|---|
| **R1** | A product with `parent_product_id` set cannot itself be a parent (max depth = 1) | App layer |
| **R2** | A parent product (has variant children) has no `stock_entry` rows — intake against a parent product is rejected | App layer |
| **R3** | `Product.default_unit_id` is required (non-null); it is the display and tracking unit | DB NOT NULL |
| **R4** | Within-dimension unit conversion uses `factor_to_base` arithmetic only — no pairwise conversion table exists (DM-8) | Architecture |
| **R5** | `ProductConversion` records cross-dimension (density) conversions only; same-dimension conversion is always `factor_to_base` math | App layer |
| **R6** | An archived product (`archived_at IS NOT NULL`) does not appear in search/intake flows but retains all children for historical integrity | App query |
| **R7** | `Unit.code` is unique per household; `Category.name` is unique per household; `Location.name` is unique per household | DB UNIQUE |

---

## Cross-context ports

Catalog is the **most upstream reference context** after Identity.

| What Catalog provides | Consumed by |
|---|---|
| `product_id`, `product_sku_id`, `unit_id`, `category_id`, `location_id` (by ID) | All contexts — Inventory lots, Pricing observations, Intake lines, Shopping items, Recipe ingredients |
| Product name, `track_stock`, `default_unit_id`, parent/variant tree (DM-19) | Inventory (fulfillment), Recipes (`ICatalogProductReader`) |
| `ProductConversion` (cross-dimension density) | Unit conversion engine (`IUnitConverter`) used by Inventory consume, Pricing normalization, Recipes authoring |
| `unit.factor_to_base` (within-dimension scaling) | Unit conversion engine — universal |
| `expiry_defaults` chain (product → category → blank) | Inventory (expiry materialization at lot creation) |
| Inline product / untracked-staple creation | Intake commit, Recipes author (`ICatalogWriter`) |

Catalog **reads nothing** from other contexts.

---

## Key decisions

- **DM-8:** `UniversalConversion` aggregate deleted. Within-dimension conversion is always `value × (factor_to_base_from / factor_to_base_to)` using the two `unit` rows that already exist. No pairwise table.
- **DM-9:** Reference data (units, categories, locations) is seeded per-household at registration. Tenancy is uniform; no global rows with nullable `household_id`.
- **DM-10:** `Product` is the rich aggregate root with `product_sku` children (pack-size variants) and `product_conversion` children (density conversions). Four expiry-default columns on `product` (ambient, after-open, after-freeze, after-thaw).
- **DM-16:** `catalog.store` is the home for resolved merchant identity — Catalog-owned reference data, deferred through Phases 1–3 and **landing in Phase 5 with Deals** ([DataModels/catalog.md](../../DataModels/catalog.md)). Phases 1–3 Pricing uses free-text `merchant_text`; `store_id` is populated from Phase 5 and back-fillable.
- **DM-19:** Product groups — `product.parent_product_id` nullable self-ref FK; abstract parents have no stock; max depth = 1.
- **Untracked staples (`track_stock = false`):** A product that is always on hand (salt, pepper) is a normal Catalog citizen but exempt from quantity accounting. Fulfillment treats it as always satisfied; Cook skips it; Shopping never auto-adds it. Can be auto-created inline from recipe authoring or Intake.

> Full schema: [../DataModels/catalog.md](../../DataModels/catalog.md)
