# Context 3 — Inventory (`inventory` schema) ✅

The ground truth for stock. `ProductStock` is the aggregate root, keyed by `(household_id, product_id)`, holding `stock_entry` lots and emitting the immutable journal. FEFO consume, transfer, freeze/thaw, and open all operate **within this one aggregate** (ADR-010).

---

**`product_stock`** — aggregate root; one row per product per household

| Column | Type | Notes |
|---|---|---|
| `household_id`, `product_id` | `uuid` | composite PK (the ADR-010 keying); `product_id` is a **soft ref** to `catalog.product` — no cross-context FK |
| `low_stock_threshold` | `numeric(12,3)` null | Per-household, per-product low stock threshold ("Running low at" in the UI). Null or zero = no threshold (never running low). When positive: `IsRunningLow` = total on-hand ≤ this value. Owned by Inventory, not Catalog. |
| `xmin` | system column | optimistic-concurrency token — Postgres' built-in row version, mapped via EF Core `.IsRowVersion()`. **No stored column, no app-side increment.** |
| `created_at` / `updated_at` | `timestamptz` | |

---

**`stock_entry`** — the lot; child of `ProductStock`

| Column | Type | Notes |
|---|---|---|
| `entry_id` | `uuid` PK | |
| `household_id`, `product_id` | `uuid` | composite **FK → `product_stock`** (within-context, enforced) |
| `sku_id` | `uuid` null | soft ref → `catalog.product_sku` |
| `quantity` | `numeric(12,3)` | remaining in this lot |
| `unit_id` | `uuid` | soft ref → `catalog.unit` |
| `location_id` | `uuid` | soft ref → `catalog.location` |
| `expiry_date` | `date` null | **materialized at event time** (see [cross-cutting-behaviour.md](cross-cutting-behaviour.md)), never computed at read time |
| `is_open` | `boolean` | set at the open event |
| `frozen_at` / `thawed_at` | `timestamptz` null | support freeze/thaw recompute (SPEC §385) |
| `purchased_at` | `date` null | shown in product detail (SPEC §1b); **price stays in Pricing** |
| `depleted_at` | `timestamptz` null | set when `quantity` reaches 0; row kept for journal integrity |
| `created_at` / `updated_at` | `timestamptz` | |

---

**`stock_journal_entry`** — immutable, append-only; the single source of truth for every **quantity** movement (ADR-011, amended)

| Column | Type | Notes |
|---|---|---|
| `journal_id` | `uuid` PK | |
| `household_id`, `product_id` | `uuid` | scoping / aggregation |
| `entry_id` | `uuid` | FK → `stock_entry` (which lot) |
| `delta` | `numeric(12,3)` | **signed** — `+` intake, `−` consume/waste — in `unit_id` |
| `unit_id` | `uuid` | soft ref → unit |
| `reason` | `text` | `Purchase` / `Consumed` / `Discarded` / `Correction` (CHECK) — ADR-011 taxonomy |
| `source_type` | `text` null | `Intake` / `Manual` / `Cook` (Phase 2) … |
| `source_ref` | `uuid` null | e.g. `import_session_id`, `cook_event_id` |
| `occurred_at` | `timestamptz` | |
| `user_id` | `uuid` | attribution (ADR-010); soft ref → identity |

No `updated_at` — rows are never updated or deleted; a correction is a *new* row.

---

## Resolved calls ✅

1. **Concurrency anchor.** `product_stock` exists primarily so a multi-lot FEFO consume can serialize on one row, preventing two concurrent consumes from over-deducting. **`SELECT … FOR UPDATE` on the root row is the authoritative write-serialization**; it closes the lost-update race directly. The concurrency *token* is Postgres' **`xmin`** system column (mapped via Npgsql EF Core's `.UseXminAsConcurrencyToken()`) — no extra column, no app-side increment — serving as the optimistic backstop for detached/UI-edit paths. An explicit trigger-maintained `bigint version` column was considered and set aside: `xmin` is a provider-blessed pattern and the project is already Postgres-committed.

2. **Transfer & Open as lot-state, not journal rows.** They don't change quantity, so they update `stock_entry` in place (location, `frozen_at`/`thawed_at`/`is_open`, recomputed `expiry_date`) rather than write a `delta`-zero journal row. The journal is the source of truth for **quantity movement**, not for every lot-lifecycle event; lot-state transitions are represented as current state on the entry. Move-history (`stock_entry_event` log) is deferred unless v1 needs it. Caveat: single `frozen_at`/`thawed_at` columns mean a freeze→thaw→refreeze cycle overwrites the prior timestamp — correct for expiry materialization, but cycle history shares the deferral.

3. **FEFO ordering & null expiry — nulls-last.** `ORDER BY expiry_date ASC NULLS LAST, created_at ASC, entry_id ASC` — a lot with no expiry is consumed *last* because null means "no expiry" (e.g. a tracked bag of rice or sugar), not "unknown." `entry_id` is the final tiebreaker so ordering is **total and deterministic** even when a bulk intake commit inserts many lots sharing an `expiry_date` and `created_at`. *(Note: a genuinely never-counted staple like salt is better modelled as an **untracked product** — `catalog.product.track_stock = false`, see below — than as a tracked null-expiry lot, which would still deplete to `Missing` over repeated cooks.)*

4. **Depleted lots retained.** A lot reaching `quantity = 0` keeps its row with `depleted_at` set (filtered from pantry views) rather than being hard-deleted. This is *required*: `stock_journal_entry.entry_id` is an enforced, append-only FK to `stock_entry`, so every historical lot must remain a live row. Long-term growth mitigation (partition or archive) is deferred.

---

> **ADR note (reconciled):** ADR-011 (amended 2026-06-06) narrows "single source of truth for every movement" to "every **quantity** movement." Quantity-neutral lot-state transitions (transfer, freeze/thaw, open) are current state on `stock_entry`; full move-history is the deferred `stock_entry_event` log.

---

## Untracked products (`catalog.product.track_stock = false`) — *(C12)*

A product flagged `track_stock = false` (salt, pepper, water) never participates in stock accounting and has **no `product_stock` / `stock_entry` rows**. The flag is read by callers; Inventory does not invent lots for it:

| Operation | Behaviour for an untracked product |
|---|---|
| **Fulfillment read** | Always reported **satisfied** — never `Missing` / `Low`. Surfaced with a distinct "staple" indicator, not a stock pip. |
| **Cook consume** | **Skipped** — no FEFO deduction, no journal row. |
| **Shopping auto-add** | Never auto-added as missing. |
| **Intake** | Not expected; an intake against an untracked product should prompt the user to enable tracking first (`track_stock = true`) rather than silently create a lot. |

Flipping `track_stock` from `false → true` later simply lets the product begin accruing `stock_entry` lots like any other; no backfill is implied. See `DataModels/catalog.md` → *Untracked staples* for the Catalog-side definition and inline auto-create.
