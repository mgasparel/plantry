# Context 5 — Pricing (`pricing` schema) ✅

A **supporting read-side context**: an append-only time-series of observed prices, written by two upstream suppliers and read for cost. Intake writes `purchase` prices on commit; Deals (Phase 3) writes `deal` prices on confirm. Recipes and Meal Planning read it for cost-per-serving (ADR-010). Keeping it out of Catalog stops a growing price series from polluting Catalog's role as stable reference data.

`PriceObservation` is the whole context — a single flat aggregate with no children. "Latest price" and "cheapest active deal" are **read models** over it, not tables.

---

**`price_observation`** — aggregate root; append-only; one row per observed price point

| Column | Type | Notes |
|---|---|---|
| `price_observation_id` | `uuid` PK | UUIDv7 |
| `household_id` | `uuid` | tenancy (RLS) |
| `product_id` | `uuid` | soft ref → `catalog.product`; **always set** — the rollup key for "latest price" and recipe costing, so every observation lands in product history even when no SKU is resolved |
| `sku_id` | `uuid` null | soft ref → `catalog.product_sku`; the specific size/variant the price was for (SPEC §2e — "price captured per SKU"). Null when the observation isn't SKU-specific |
| `source` | `text` | `purchase` / `deal` (CHECK) — ADR-010 taxonomy. `purchase` written by Intake, `deal` by Deals (Phase 3) |
| `price` | `numeric(12,2)` | the money observed — total paid for `quantity` (purchase) or the advertised sale price for the deal pack |
| `quantity` | `numeric(12,3)` | the amount `price` was for (line qty / pack size) — needed to normalize |
| `unit_id` | `uuid` | soft ref → `catalog.unit`; the unit of `quantity` |
| `unit_price` | `numeric(12,4)` null | **materialized at write time**: `price / quantity` normalized to the base unit of `unit_id`'s dimension (via the conversion engine), so observations of different pack sizes are comparable. **Null** if the conversion can't be resolved (cross-dimension without a `product_conversion`) — read models then fall back to raw `price` / `quantity`. Mirrors expiry materialization |
| `merchant_text` | `text` null | merchant as observed — Intake's `import_session.merchant_text` (purchase) or the flyer store name (deal). Free-text provenance; **the only merchant data Phase 1 has** |
| `store_id` | `uuid` null | soft ref → `catalog.store` (resolved merchant identity). **Null in Phase 1** (no `store` table yet); populated by Deals (Phase 3) and optionally back-filled. DM-16 |
| `valid_from` | `date` null | deal validity window start; **null for `purchase`** (a purchase is a point observation at `observed_at`) |
| `valid_to` | `date` null | deal validity window end — drives the "cheapest *active* deal" read model (`source='deal' AND valid_to >= today`). Null for purchase |
| `source_ref` | `uuid` null | provenance soft ref to the writer's record: `intake.import_line` (purchase) or `deals.deal` (deal). Supports audit + de-dup |
| `observed_at` | `timestamptz` | when the price was true — receipt purchase time, or deal capture time |
| `user_id` | `uuid` null | attribution for user-initiated observations (soft ref → identity); null for system/async writes |
| `created_at` | `timestamptz` | insert time (provenance, distinct from `observed_at`) |

No `updated_at` — the table is **append-only** (DM-4). A wrong observation is superseded by a newer row, never edited or deleted; "latest" naturally wins the read models.

---

## Read models (not tables)

- **Latest price** per product (or per SKU): `DISTINCT ON (product_id) … ORDER BY product_id, observed_at DESC`. Backing index `(household_id, product_id, observed_at DESC)` — and `(household_id, sku_id, observed_at DESC)` for the SKU-level view.
- **Cheapest active deal**: `WHERE source='deal' AND valid_from <= :today AND valid_to >= :today`, `MIN(unit_price)` per product. Partial index on `(household_id, product_id) WHERE source='deal'`.
- **Cost per serving** (Recipes, Phase 2) composes Recipe × these two read models — the two SPEC §4 tiers: *purchase-history* (latest/representative purchase `unit_price`) and *deal-aware* (cheapest active-deal `unit_price`).

---

## Resolved calls ✅

1. **Keyed on product, refined by SKU.** `product_id` is mandatory, `sku_id` optional. Cost-per-serving and "latest price" roll up at the product level (fulfillment and costing are product-level), but price genuinely varies by SKU — a 2 L vs 4 L milk have different unit economics. Mandatory `product_id` means a receipt line with no resolved SKU still contributes to product price history; `sku_id` sharpens it when known. Mirrors `inventory.stock_entry`'s product-plus-optional-SKU shape.

2. **Normalize at write (`unit_price`), not at read — and fail *soft*.** Comparing $4.00/2 L against $5.00/4 L needs per-unit normalization. Doing it once at write (materialized, like `stock_entry.expiry_date`) keeps the "cheapest"/"latest" read models simple and puts the one conversion lookup where Catalog context is already loaded (Intake's commit, Deals' confirm). **Divergence from the Consume rule:** [cross-cutting-behaviour.md](cross-cutting-behaviour.md) has unit conversion *fail loudly* — correct for `Consume`, where silently mis-deducting stock is unacceptable. Here a missing density (cross-dimension, no `product_conversion`) must **not** block recording a legitimate price, so Pricing fails soft: `unit_price` is left null and read models fall back to raw `price`/`quantity`. Rejected: re-deriving per-unit price at every read (repeated conversion + repeated failure handling spread across Recipes and Meal Planning).

3. **DM-16 finalized — merchant identity is `merchant_text` *and* `store_id`.** The observation carries both: free-text `merchant_text` (always — the as-observed provenance, and the only merchant data Phase 1 produces) and a nullable `store_id` soft-ref to a `catalog.store` reference aggregate (the resolved identity). **`store` belongs in Catalog**, not Deals — it is stable per-household reference data of the same shape as `Location`/`Unit`/`Category`, referenced by Intake, Pricing, Shopping, and Deals. Placing it in Catalog removes the phase-inversion where Pricing (Phase 1→2) would otherwise depend on Deals (Phase 3). The `store` **table itself lands with Deals/Phase 3** (Phase 1 has no merchant-management UI and no need for it), so in Phase 1 `store_id` stays null and `merchant_text` carries the value; `store_id` can be back-filled when `store` exists. This answers the placeholder's open question (free-text *or* soft-ref → **both**).

4. **Deal validity window lives on the observation.** "Cheapest active deal" is a Pricing read model consumed by Recipes/Meal Planning, and per ADR-010 no context reads another's tables — so the active window must be queryable *within* Pricing. Hence `valid_from`/`valid_to` sit on `price_observation` (null for purchases). The rich `deal` aggregate (Deals, Phase 3) stays the source record; confirming a deal projects a `price_observation` carrying its window. Rejected: keeping the window only in Deals and forcing Recipes to join across context boundaries.

5. **Flat, append-only, no children — so no composite-FK plumbing.** `PriceObservation` is an immutable log (DM-4), not a parent aggregate, so it needs no `UNIQUE (household_id, id)` — nothing FKs into it within-context. Intake's `import_line.committed_price_observation_id` is a **cross-context soft ref** (no enforced FK, per DM-3). Corrections are new rows; there is no in-place edit path.

---

> **Writers.** Intake commit (intake.md §commit orchestration) calls Pricing **record-observation** with `source = purchase`, `merchant_text` from `import_session.merchant_text`, `source_ref = import_line_id`, returning the id stored as `import_line.committed_price_observation_id`. Deals (Phase 3) calls it on deal confirm with `source = deal` and the validity window.

> **ADR note (reconciled).** ADR-010's amendment left DM-16 "to be finalized when the Pricing context is modeled." **Finalized here:** `store` is a **Catalog** reference aggregate (table deferred to Phase 3 with Deals); `price_observation` carries `merchant_text` (populated Phase 1) plus a nullable `store_id` soft-ref (populated Phase 3+). This supersedes the unamended fallback line in ADR-010 §Aggregates, "Deals — `Store` (configured merchants)."
