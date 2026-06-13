# Inventory — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 1 (backfilled)
>
> **Purpose:** The shared vocabulary for the Inventory bounded context. Terms here — `StockEntry`, `Consume`, `FEFO`, `journal` — appear verbatim in code, schema, and in the cross-context surface (`IInventoryConsumer`, `IInventoryStockReader`) used by Recipes and Shopping.
>
> **Bounded context:** Inventory (`inventory` schema, Phase 1).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregates & Entities

| Term | Kind | Definition |
|---|---|---|
| **ProductStock** | Aggregate root | The in-stock position for one product in one household. Keyed by `(household_id, product_id)`. The concurrency anchor for all writes to a product's lots. |
| **StockEntry** | Entity (child of ProductStock) | A single lot of stock — a specific quantity of a product, at a location, with an expiry date. Mutable: quantity decreases as stock is consumed. Retained when depleted. Also called a **lot**. |
| **StockJournalEntry** | Entity (child of ProductStock) | An immutable, append-only record of a quantity movement. The source of truth for every `+` or `−` delta. Never updated or deleted. |

---

## Key Terms

| Term | Definition |
|---|---|
| **Lot** | A `StockEntry` — one identifiable batch of stock at a specific location with its own expiry date. |
| **FEFO** | First-Expired-First-Out. The ordering rule for consumption: consume the lot with the earliest `expiry_date` first. Null expiry is consumed last (means "no expiry," not "unknown"). |
| **Consume** | Deduct a quantity of a product from stock, FEFO-ordered. The single ADR-011 primitive. Never blocks when stock is insufficient — consumes whatever is available. |
| **Delta** | The signed quantity change on a `StockJournalEntry`: positive for intake/purchase, negative for consume/waste. In the lot's `unit_id`. |
| **Reason** | The categorization of a journal entry's delta: `Purchase` / `Consumed` / `Discarded` / `Correction`. |
| **Source type** | What triggered the journal entry: `Intake` / `Manual` / `Cook` (Phase 2). |
| **Source ref** | The UUID of the originating record (e.g., `import_session_id`, `cook_event_id`) — provenance. |
| **Depleted lot** | A `StockEntry` whose `quantity` has reached 0. `depleted_at` is set; the row is retained (journal FK requires it); it is filtered from pantry views. |
| **Transfer** | Move a lot to a different storage location. Recomputes `expiry_date` if the location type changes (frozen ↔ ambient). Does **not** write a journal row. |
| **Freeze** | Set a lot's `frozen_at`; recomputes `expiry_date` from `default_due_days_after_freezing`. Lot-state change — no journal delta. |
| **Thaw** | Set a lot's `thawed_at`; recomputes `expiry_date` from `default_due_days_after_thawing`. Lot-state change — no journal delta. |
| **Open** | Set `is_open = true` on a lot; recomputes `expiry_date` from `default_due_days_after_opening`. Lot-state change — no journal delta. |
| **Waste / Discard** | A consume whose `reason = Discarded` — stock the user is throwing away, not using. Produces a `−delta` journal row; marks lot `depleted_at`. |
| **Correction** | A new journal row (`reason = Correction`) that adjusts quantity without a real-world event (e.g., re-count). Corrections are new rows; they never mutate existing entries. |
| **Fulfillment** | Whether a product (or recipe ingredient) is sufficiently in stock. Read-side concern: Inventory provides `IInventoryStockReader`; Recipes computes `FulfillmentResult` from it. |
| **`xmin`** | The Postgres system column used as an optimistic concurrency token on `product_stock`. No stored version column needed. |

---

## Key Actions

| Verb | Meaning |
|---|---|
| **Record purchase** | Create a new `StockEntry` lot and write a `+delta Purchase` journal row. Done by Intake commit. |
| **Consume** | FEFO-deduct a quantity, writing `−delta Consumed` journal rows. Done by CookRecipe or manual UI. |
| **Transfer** | Move a lot between locations; recompute expiry if the location type changes. |
| **Freeze / Thaw** | Change a lot's frozen state; recompute expiry. |
| **Open** | Mark a lot as opened; recompute expiry from the `after_opening` default. |
| **Discard** | Write a `−delta Discarded` journal row for stock being thrown away. |
| **Correct** | Write a `+/− Correction` journal row to adjust a lot's quantity after a re-count. |

---

## Cross-context terms (received by ID, owned by Catalog)

| Term | Notes |
|---|---|
| `product_id` | Soft-ref to `catalog.product`. Never a cross-schema FK. |
| `unit_id` | Soft-ref to `catalog.unit`. Used for lot quantity and journal delta. |
| `location_id` | Soft-ref to `catalog.location`. Location type (`frozen`) drives freeze/thaw expiry logic. |
| `sku_id` | Soft-ref to `catalog.product_sku`. Optional on `stock_entry` — tracked for provenance but not for stock aggregation (always product-level). |
| `track_stock` | Flag on `catalog.product`. If false, Inventory ignores the product entirely (no lots, no journal rows). |
