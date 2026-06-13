# Inventory — Domain Model

> **Status:** Implemented — Phase 1. Retrospective backfill; the DDD process was formalized after these contexts were designed.
>
> **Purpose:** The ground truth for what is physically on hand. Owns the stock lifecycle from purchase through consumption, transfer, freeze/thaw, and waste. Exposes a single `Consume` primitive that all callers use (ADR-011).
>
> **Bounded context:** Inventory (`inventory` schema, Phase 1). Reads Catalog by ID for unit conversion and product metadata; never reads Intake, Pricing, or Shopping.
>
> **Code shape:** `ProductStock` is the aggregate root (one per product per household), holding `StockEntry` lots and emitting immutable `StockJournalEntry` records. Optimistic concurrency via Postgres `xmin`; write serialization via `SELECT … FOR UPDATE`.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregate map

| Aggregate root | Identity | Owns (composition) | Lifecycle |
|---|---|---|---|
| **ProductStock** | `(household_id, product_id)` composite | `StockEntry[]` (lots), `StockJournalEntry[]` (immutable) | One root per product per household; created on first intake; never deleted |

`StockEntry` (a lot) is a mutable child — quantity decreases as stock is consumed; it gains `depleted_at` when empty but is **never hard-deleted** (journal FK integrity). `StockJournalEntry` is append-only — no update, no delete; a correction is a new row.

### Keying

`ProductStock` is keyed by `(household_id, product_id)` — the product ID is a **soft-ref** to `catalog.product` (no enforced cross-context FK). There is no surrogate PK on `product_stock`; the composite is the identity.

---

## Invariants

| # | Invariant | Enforced |
|---|---|---|
| **R1** | `StockEntry` always references a **non-parent** product (`product_id` must not be a Catalog parent-product) | App layer (intake rejects parent products) |
| **R2** | FEFO ordering: `expiry_date ASC NULLS LAST, created_at ASC, entry_id ASC` — null expiry is consumed last ("no expiry," not "unknown") | App layer + DB index |
| **R3** | `StockJournalEntry` is **append-only** — no update, no delete. A correction is a new row with signed `delta` | Architecture |
| **R4** | Depleted lots (`quantity = 0`) are **retained** with `depleted_at` set; hard-delete is not permitted because journal entries FK into them | Architecture |
| **R5** | Write serialization: every multi-lot consume does `SELECT … FOR UPDATE` on the `ProductStock` root before deducting from lots — prevents over-deduction under concurrent consumes | App service |
| **R6** | Transfer, freeze, thaw, and open are **lot-state transitions** — they update `stock_entry` in place; they do **not** write a journal row because they don't change quantity (DM-14) | App service |
| **R7** | An untracked product (`catalog.product.track_stock = false`) has **no** `product_stock` or `stock_entry` rows; Inventory ignores it | App layer |

---

## Domain & Application Services

| Service | Responsibility |
|---|---|
| **Consume** (the primitive, ADR-011) | `Consume(productId, quantity, unit, reason, userId, sourceType, sourceRef)` → deducts FEFO-ordered from lots, writes journal rows, updates `product_stock.updated_at`. Never blocks on shortfall — consumes whatever is available. Called by Intake commit (Purchase), manual consume UI, and CookRecipe (Cook). |
| **RecordPurchase** | Intake commit creates a new `StockEntry` lot and writes a `+delta` journal row (`reason = Purchase`). |
| **Transfer** | Moves a lot to a different `Location`; recomputes `expiry_date` if the new location is frozen or was previously frozen/thawed. |
| **Freeze / Thaw** | Sets `frozen_at` / `thawed_at`; recomputes `expiry_date` from the product's `default_due_days_after_freezing` / `after_thawing`. |
| **Open** | Sets `is_open = true`; recomputes `expiry_date` from `default_due_days_after_opening`. |
| **Discard (waste)** | Writes a `−delta` journal row with `reason = Discarded` and sets `depleted_at` on the lot. |

---

## Cross-context ports

| Port | Direction | What's exchanged |
|---|---|---|
| **`IInventoryStockReader`** | Provided to Recipes, Shopping | Available quantity + soonest expiry per product; variant rollup for parent-product ingredients (DM-19) |
| **`IInventoryConsumer`** | Provided to Recipes | The single `Consume` primitive (ADR-011) — `CookRecipe` calls it per tracked ingredient |
| Catalog (`unit`, `product`) | Reads by ID | `factor_to_base` for unit conversion in `Consume`; `track_stock` to skip untracked products; parent/variant tree for fulfillment rollup |

---

## Key decisions

- **DM-13:** `ProductStock` composite-keyed root; `stock_entry` lots; immutable `stock_journal_entry`. Optimistic concurrency via Postgres `xmin`; `FOR UPDATE` as the write-serialization gate.
- **DM-14:** Journal is source of truth for **quantity movement only**. Lot-state transitions (transfer, freeze, thaw, open) are current state on `stock_entry`; move-history log (`stock_entry_event`) is deferred.
- **FEFO nulls-last (DM-13):** Null expiry = "no expiry" (bag of rice) — consumed last, not first. `entry_id` as final tiebreaker ensures total deterministic ordering.
- **Depleted-lot retention (DM-13):** Required by the enforced `stock_journal_entry → stock_entry` FK. Historical lots must remain live rows.
- **Single Consume primitive (ADR-011):** All callers — Intake, manual UI, Recipes — use one surface. The primitive never blocks on shortfall; it consumes whatever is available.

> Full schema: [../DataModels/inventory.md](../../DataModels/inventory.md)
> Cross-cutting expiry and consume behaviour: [../DataModels/cross-cutting-behaviour.md](../../DataModels/cross-cutting-behaviour.md)
