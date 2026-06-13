# Context 2 — Catalog (`catalog` schema) ✅

The universal upstream supplier; every other context references its entities **by ID only**.

**Two simplifications adopted:**

1. **`UniversalConversion` aggregate deleted.** Every within-dimension conversion is linear scaling, fully captured by a `factor_to_base` column on `unit` (base unit per dimension has factor `1`). There is **no pairwise conversion table** — e.g. `kg→g` is `value × (factor_to_base[kg] / factor_to_base[g]) = ×1000`, computed from the two unit rows that exist anyway. *(Amends ADR-010, which listed `UniversalConversion` as an aggregate.)*
2. **Reference data seeded per-household.** Units, categories, and locations are seeded into each household on creation rather than held as global rows with a nullable `household_id` — keeps tenancy uniform and RLS simple.

---

**`unit`**

| Column | Type | Notes |
|---|---|---|
| `unit_id` | `uuid` PK | |
| `household_id` | `uuid` | |
| `code` | `text` | short, e.g. `g`, `ml`, `ea`; `UNIQUE (household_id, code)` |
| `name` | `text` | full, "gram" |
| `dimension` | `text` | `mass` / `volume` / `count` (CHECK) |
| `factor_to_base` | `numeric(18,6)` | multiplier to the dimension's base unit (base = `1`) |

---

**`category`**

| Column | Type | Notes |
|---|---|---|
| `category_id` | `uuid` PK | |
| `household_id` | `uuid` | |
| `name` | `text` | `UNIQUE (household_id, name)` |
| `default_due_days` | `int` null | category-level expiry fallback (SPEC §7d) |
| `sort_order` | `int` | store-layout grouping (SPEC §3a) |

---

**`location`**

| Column | Type | Notes |
|---|---|---|
| `location_id` | `uuid` PK | |
| `household_id` | `uuid` | |
| `name` | `text` | `UNIQUE (household_id, name)` |
| `type` | `text` | `frozen` / `ambient` (CHECK); `frozen` triggers freeze/thaw logic |

---

**`product`** — the rich aggregate root

| Column | Type | Notes |
|---|---|---|
| `product_id` | `uuid` PK | + `UNIQUE (household_id, product_id)` for child composite FKs |
| `household_id` | `uuid` | |
| `name` | `text` | |
| `parent_product_id` | `uuid` null | FK → `product (household_id, product_id)` — self-ref; null = this is a parent or standalone product. `CHECK (parent_product_id IS NULL OR product_id <> parent_product_id)`. A product whose `parent_product_id` is non-null must not itself be referenced as a `parent_product_id` by any other row (max depth 1 — enforced in app layer; DB-level enforcement via trigger is deferred). |
| `category_id` | `uuid` null | FK → `category` (uncategorized allowed) |
| `default_unit_id` | `uuid` | FK → `unit`; display/tracking unit (may differ from the parent's) |
| `default_location_id` | `uuid` null | FK → `location`; pre-fills intake |
| `default_due_days` | `int` null | expiry default — ambient, non-open (SPEC §7a) |
| `default_due_days_after_opening` | `int` null | |
| `default_due_days_after_freezing` | `int` null | |
| `default_due_days_after_thawing` | `int` null | |
| `track_stock` | `boolean` NOT NULL DEFAULT `true` | `false` = **untracked staple** (salt, pepper, water): catalogued and costable, but exempt from quantity accounting. See *Untracked staples* below and DM/C12 in `DomainDesign/recipes-journeys.md`. |
| `archived_at` | `timestamptz` null | soft delete |
| `created_at` / `updated_at` | `timestamptz` | |

**Product group rules (enforced in app layer):**
- A product with `parent_product_id IS NULL` and at least one variant child is a *parent product* — abstract; no `stock_entry` rows may reference it.
- A product with `parent_product_id IS NOT NULL` is a *variant*. Variants cannot themselves be parents (max depth = 1).
- `StockEntry` and `StockJournalEntry` always reference a non-parent `product_id`. The app layer rejects intake against a parent product.

---

**`product_sku`** — child of `Product`; optional (stock aggregates at product level; price/intake reference SKU)

| Column | Type | Notes |
|---|---|---|
| `sku_id` | `uuid` PK | |
| `household_id`, `product_id` | `uuid` | composite FK → `product (household_id, product_id)` |
| `label` | `text` | "2 L carton", "500 g bag" |
| `size_quantity` | `numeric(12,3)` null | pack-size amount |
| `size_unit_id` | `uuid` null | FK → `unit` |

---

**`product_conversion`** — child of `Product`; **cross-dimension / density only** (SPEC §7a)

| Column | Type | Notes |
|---|---|---|
| `conversion_id` | `uuid` PK | |
| `household_id`, `product_id` | `uuid` | composite FK → `product` |
| `from_unit_id` / `to_unit_id` | `uuid` | FK → `unit` |
| `factor` | `numeric(18,6)` | 1 `from_unit` of *this product* = `factor` × `to_unit` |

---

**Dividing line for conversions:** anything *universal* (`kg = 1000 g`, `dozen = 12`) is a `factor_to_base` value on `unit`; anything that *depends on the product* (`1 cup flour = 120 g`, `1 egg = 50 g`) is a `product_conversion` row.

---

**Untracked staples (`track_stock = false`)** — *(C12)*

A staple the household always has on hand (salt, pepper, water, oil) is a normal `product` row with `track_stock = false`. It is a full Catalog citizen — it can carry `product_sku` rows and accrue price history — but it never receives `stock_entry` lots and is exempt from quantity accounting. Inventory and recipe flows interpret the flag (see `DataModels/inventory.md`):

- **Fulfillment** treats it as **always satisfied** (never `Missing` / `Low`).
- **Cook consume** **skips** it (no FEFO deduction).
- **Shopping** never auto-adds it as missing.

**Inline auto-create:** when a recipe author (or any inline-create surface) types a name that matches no product, the system mints an untracked product from just the name — `default_unit_id` defaults to the unit on the originating line, or the household `ea` unit if none — and warns the user. The user can later open the product and set `track_stock = true` to begin tracking it like any other product. This is the Catalog-side mechanism behind VISION Pillar 2's "unrecognized items can be created inline without breaking the flow."
