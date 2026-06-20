# Take Stock — Data Schema

> **Status:** Design in progress — Phase 2 (bd `plantry-5vxb`)
>
> **Purpose:** What the database needs for Take Stock. The headline: **no migration is required for correctness** — the existing `inventory` schema already permits everything Take Stock writes. The **one decided change is a single index migration** (`ix_stock_entry_by_location`) for the location-walk read. Builds on [inventory.md](../DataModels/inventory.md), the [domain model](inventory-takestock-domain-model.md), and the [journeys](inventory-takestock-journeys.md). Verified against the live migrations, not the prose schema doc.
>
> **Bounded context:** Inventory (`inventory` schema); one read touches Catalog (`catalog.product`).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model  →  Data Schema (← here)  →  App Services  →  UI Slices
```

---

## Verified: the schema already supports Take Stock

Checked against `20260609104359_InitialInventorySchema` and `20260609200500_AddSourceTypeCheckConstraint`:

| Need (domain model) | Schema reality | Migration? |
|---|---|---|
| **Positive `Correction`** for upward counts / opening balance (TS-2, C8) | `ck_stock_journal_entry_reason` allows `Correction`; `delta numeric(12,3)` has **no sign constraint** and **no sign-vs-reason rule**. A `+` `Correction` row is already legal. | **None** |
| **Downward `Correction` / `Consumed` / `Discarded`** (C9) | All four reasons in the CHECK; `Consume` already writes `−delta` rows for these. | **None** |
| **`source_type = Manual`** on every Take Stock row (TS-1) | `ck_stock_journal_entry_source_type` allows `Manual`. | **None** |
| **Location-scoped consume** (TS-3) | `stock_entry.location_id` exists (`uuid NOT NULL`); the FEFO filter is an app-layer `WHERE location_id = …` over a product's lots. | **None** (correctness); see index below |
| **New lot for found stock** (TS-4) | `AddStock` already inserts `stock_entry` rows; nothing lot-shaped is new. | **None** |
| **No count session / draft** (C7, TS-1) | Nothing to persist — pending counts are page-only; only journal rows land. | **None** (no new table) |
| **Counted unit on the journal row** (TS-5) | `stock_journal_entry.unit_id` is per-row; already stores whatever unit the movement used. | **None** |

**Conclusion:** Take Stock's *writes* require **zero schema migrations**. It rides entirely on the existing `product_stock` / `stock_entry` / `stock_journal_entry` shape, RLS policies, and CHECK constraints. The single migration it does ship is the read-side index below.

---

## The one schema change — a location index (decided)

The **location-walk listing** (J2) reads differently from anything Inventory does today: *"all active lots **at location L**, grouped/summed by product."* Existing `stock_entry` indexes are:

- `ix_stock_entry_fefo` — `(household_id, product_id, expiry_date, created_at)` — product-first; great for a single product's FEFO consume (TS-3 still uses this), useless for a location sweep.
- `IX_stock_entry_household_id` — `(household_id)` — too coarse for a per-location scan.

There is **no index on `location_id`**. Without one the walk scans all of a household's active lots and filters by location.

**Decided: add `ix_stock_entry_by_location` on `(household_id, location_id, product_id)`, partial `WHERE depleted_at IS NULL`** (the walk only ever sums active lots). It turns the per-location sweep into an index range scan and supports the per-(product, Location) group/sum directly. It ships **with the feature** in the same slice as the location-walk read — a one-line, low-risk migration, not deferred. (It remains a performance change, so it carries no correctness risk if the read lands first.)

---

## Catalog side (TS-9 / read)

- **`SetDefaultLocationCommand`** (TS-9) writes the existing `catalog.product.default_location_id` via `Product.SetDefaultLocation` — **no Catalog migration**.
- **Walk listing branch (b)** ("products defaulted to L with no lots here") and the **"No location" listing** (J7) filter `catalog.product.default_location_id` (`= L` / `IS NULL`) with `track_stock = true`, non-parent, non-archived. These are small per-household Catalog scans; an index on `default_location_id` is **not** warranted at MVP volumes (note for later if Catalog grows).

---

## RLS / tenancy

Unchanged. `product_stock`, `stock_entry`, `stock_journal_entry` already `ENABLE`/`FORCE ROW LEVEL SECURITY` with the `household_isolation` policy keyed on `app.household_id`; Catalog tables likewise. Take Stock adds no table, so no new policy. Every read and write is household-scoped by the existing backstop.

---

## Decisions (summary)

- **TS-S1** No migration is required for correctness — positive `Correction`, `Manual` source, and per-row units are all already schema-legal.
- **TS-S2** **Add `ix_stock_entry_by_location` on `(household_id, location_id, product_id)`, partial `WHERE depleted_at IS NULL`** to serve the location-walk listing. Decided; ships with the feature in the location-walk read slice.
- **TS-S3** No Catalog migration — `SetDefaultLocationCommand` reuses `default_location_id`; listing filters need no new index at MVP volumes.

---

## Open for later passes

- **App-service pass:** finalize where the location-walk read assembles (the `ITakeStockReader` vs inline-Web question from TS-10) and confirm the exact group/sum SQL the index above should serve.
- Revisit the `default_location_id` index only if Catalog product counts per household grow well beyond MVP expectations.
