# Pricing — Domain Model

> **Status:** Implemented — Phase 1. Retrospective backfill; the DDD process was formalized after these contexts were designed.
>
> **Purpose:** A supporting read-side context: an append-only time-series of observed prices. Intake writes `purchase` prices on commit; Deals (Phase 3) will write `deal` prices on confirm. Recipes and Meal Planning read it for cost-per-serving. Keeping it out of Catalog prevents a growing price series from polluting stable reference data.
>
> **Bounded context:** Pricing (`pricing` schema, Phase 1→2). No children, no mutable state. "Latest price" and "cheapest active deal" are **read models** over `PriceObservation`, not tables.
>
> **Code shape:** `PriceObservation` is the whole context — a single flat, append-only aggregate. The two read models are query projections backed by partial indexes.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregate map

| Aggregate root | Identity | Owns | Lifecycle |
|---|---|---|---|
| **PriceObservation** | `PriceObservationId` (UUIDv7) | — (flat, no children) | **Append-only.** Never updated or deleted. A wrong observation is superseded by a newer row. |

Pricing is unusual: the entire context is one aggregate type with no children, no mutable state, and no lifecycle transitions. All intelligence lives in two read models built from the raw log.

---

## Read models

| Read model | Definition | Use |
|---|---|---|
| **Latest price** | `DISTINCT ON (product_id) … ORDER BY product_id, observed_at DESC` | Recipes cost-per-serving (purchase-history tier) |
| **Cheapest active deal** | `WHERE source='deal' AND valid_from ≤ today AND valid_to ≥ today`, `MIN(unit_price)` per product | Recipes cost-per-serving (deal-aware tier) — Phase 2+ |

These are queries, not tables. No context reads Pricing's table directly (ADR-010) — they call Pricing's read-model application services.

---

## Invariants

| # | Invariant | Enforced |
|---|---|---|
| **R1** | `PriceObservation` rows are **never updated or deleted**. A correction is a new row. | Architecture |
| **R2** | `product_id` is always set — the mandatory rollup key. A price without a matched product cannot exist. | DB NOT NULL |
| **R3** | `unit_price` is materialized at write time: `price / quantity` normalized to the dimension's base unit. Fails **soft** on cross-dimension (null) — a missing `ProductConversion` must not block recording the price. | App service |
| **R4** | `valid_from` / `valid_to` are null for `source = purchase`; they are set only for `source = deal`. | App service |
| **R5** | `source_ref` is a soft-ref only (no enforced cross-context FK). `import_line.committed_price_observation_id` is the reverse soft-ref. | Architecture (DM-3) |

---

## Cross-context ports

| Direction | Context | What's exchanged |
|---|---|---|
| Written by | **Intake** | `RecordObservation(source=purchase, merchant_text, source_ref=import_line_id)` on commit |
| Written by | **Deals** (Phase 3) | `RecordObservation(source=deal, valid_from, valid_to, source_ref=deal_id)` on deal confirm |
| Read by | **Recipes** | `IPriceReader` — latest purchase unit price + cheapest active deal per product for `CostPerServing` |
| Read by | **Meal Planning** (Phase 2) | Same cost surfaces |
| Soft-refs in | Catalog | `product_id`, `sku_id`, `unit_id`, `store_id` (Phase 3, nullable now) |

---

## Key decisions

- **DM-17:** Flat, append-only, no children — no composite-FK plumbing. `PriceObservation` is an immutable log; nothing FKs into it within-context.
- **Normalize at write, fail soft (DM-17):** `unit_price` is materialized once at write (mirrors expiry materialization in Inventory). Unlike the `Consume` primitive (which fails **loudly** on a missing conversion because mis-deducting stock is unacceptable), Pricing fails **soft** — a missing cross-dimension `ProductConversion` leaves `unit_price` null and read models fall back to raw `price/quantity`. A missing density must not block recording a legitimate price.
- **DM-16 (finalized):** `store_id` (soft-ref to `catalog.store`) exists on the row but is null in Phase 1. `merchant_text` is the only merchant data until Phase 3. When `catalog.store` exists (Phase 3), `store_id` can be back-filled.
- **Deal validity window on the row (DM-17):** The "cheapest active deal" read model must be queryable entirely within Pricing. Hence `valid_from`/`valid_to` are on `price_observation`, not only on the `deals.deal` aggregate.

> Full schema: [../DataModels/pricing.md](../DataModels/pricing.md)
