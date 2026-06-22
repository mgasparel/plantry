# Pricing — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 1 (backfilled)
>
> **Purpose:** The shared vocabulary for the Pricing bounded context. Key terms — `PriceObservation`, `unit_price`, `merchant_text`, read models — appear in the ports exposed to Recipes and in the write-side contract given to Intake and Deals.
>
> **Bounded context:** Pricing (`pricing` schema, Phase 1→2).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregates

| Term | Kind | Definition |
|---|---|---|
| **PriceObservation** | Aggregate root (append-only) | One observed price point. Immutable once written — the full history of what a product cost, when and where. The entire Pricing context is this one type. |

---

## Key Terms

| Term | Definition |
|---|---|
| **`source`** | Whether the observation is a `purchase` (written by Intake commit) or a `deal` (written by Deals on confirm, Phase 5). |
| **`unit_price`** | The normalized per-base-unit price: `price ÷ quantity`, converted to the dimension's base unit via `factor_to_base`. Materialized at write. Null when cross-dimension conversion is unavailable (fails soft — see below). |
| **`observed_at`** | When the price was true: receipt purchase time (for `purchase`) or deal capture time (for `deal`). Distinct from `created_at` (when the row was inserted). |
| **`merchant_text`** | Free-text merchant name as observed on the receipt or flyer. The only merchant data in Phase 1. Propagated from `ImportSession.merchant_text` on commit. |
| **`store_id`** | Soft-ref to `catalog.store` — the resolved merchant identity. **Null in Phase 1** (no `store` table yet). Will be populated by Deals (Phase 5) and back-filled where possible. |
| **`valid_from` / `valid_to`** | Deal validity window — null for `purchase` (a point observation). Set only for `source = deal`; drives the "cheapest active deal" read model. |
| **`source_ref`** | Soft-ref provenance: `import_line_id` for purchase, `deal_id` for deal. Supports audit and de-duplication. |
| **Fail soft** | When `unit_price` cannot be materialized (no `ProductConversion` for cross-dimension), the field is left null rather than blocking the write. Read models fall back to raw `price`/`quantity`. Contrasts with the Consume primitive's fail-loud rule. |
| **Latest price** | Read model: the most recent `PriceObservation` per product (or per SKU), by `observed_at DESC`. Used for Recipes cost-per-serving purchase-history tier. |
| **Cheapest active deal** | Read model: `MIN(unit_price)` where `source='deal'` and `valid_to ≥ today` and `valid_from ≤ today`, per product. Used for Recipes deal-aware cost tier (Phase 2+). |
| **Cost per serving** | Recipes value object composed from these two read models — not a Pricing term, but Pricing provides its inputs via `IPriceReader`. |

---

## Key Actions

| Verb | Meaning |
|---|---|
| **Record observation (purchase)** | Intake commit calls this with `source=purchase`, `merchant_text`, the resolved `product_id`/`sku_id`, `quantity`, `unit_id`, `price`. Materializes `unit_price` at write. |
| **Record observation (deal)** | Deals (Phase 5) calls this with `source=deal`, the validity window, and deal provenance. |
| **Read latest price** | Application service query used by Recipes for cost-per-serving (purchase tier). |
| **Read cheapest active deal** | Application service query used by Recipes for cost-per-serving (deal tier) and Shopping for deal badges. |

---

## Cross-context terms (owned by others, referenced by Pricing)

| Term | Owned by | Notes |
|---|---|---|
| `product_id` | Catalog | Mandatory rollup key on every observation |
| `sku_id` | Catalog | Optional — sharpens price to a specific pack size |
| `unit_id` | Catalog | The unit `quantity` is expressed in; used with `factor_to_base` to compute `unit_price` |
| `store_id` | Catalog (`catalog.store`, Phase 5) | Resolved merchant; null until Phase 5 |
| `import_line_id` | Intake | Provenance soft-ref for purchase observations |
