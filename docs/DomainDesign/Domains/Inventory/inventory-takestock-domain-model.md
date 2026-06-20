# Take Stock — Domain Model

> **Status:** Design in progress — Phase 4 (bd `plantry-5vxb`)
>
> **Purpose:** How Take Stock is built on the existing Inventory aggregate. It introduces **no new aggregate** — every count resolves to operations on `ProductStock` and its immutable journal. This pass pins down the few **aggregate changes** required, the **count → domain-operation mapping**, the application services, the read models, and the concurrency/idempotency story. Builds on [inventory-domain-model.md](inventory-domain-model.md), the [journeys](inventory-takestock-journeys.md), and the [ubiquitous language](inventory-takestock-ubiquitous-language.md). Feeds the data-schema and app-service passes.
>
> **Bounded context:** Inventory (`inventory` schema). Writes Catalog only through an anti-corruption port (TS-8/TS-9).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## No new aggregate

Take Stock reads and writes the existing **`ProductStock`** aggregate (root per `(household_id, product_id)`), its `StockEntry` lots, and the append-only `StockJournalEntry`. A "count" is a reconciling **delta** turned into journal rows — exactly the `Correction` / `Consumed` / `Discarded` taxonomy that already exists. There is **no** persisted count session (C7), so no session aggregate.

What *does* change is small and surgical: two methods on the Inventory aggregate get a parameter each, and Catalog gains one focused command. Everything else is application-layer orchestration and read models.

---

## Required domain changes

| # | Change | Where | Why |
|---|---|---|---|
| **TS-2** | **Generalize the positive path.** Add `StockReason reason = Purchase` to `ProductStock.AddStock`, guarded by a new `StockReason.IsAddition()` (true for `Purchase` and `Correction` only). Intake keeps passing `Purchase`; Take Stock passes `Correction`. | `Plantry.Inventory.Domain` (`ProductStock`, `StockReason`) | Closes the upward-correction gap (C8). Today `AddStock` hard-codes `Purchase` and `Consume` rejects positive deltas, so "found more / opening balance" has no clean home. Symmetric with `Consume`'s existing `IsRemoval()` guard. |
| **TS-3** | **Location-scoped consume.** Add `Guid? locationId = null` to `ProductStock.Consume`; when set, the FEFO candidate set is filtered to lots at that Location. `null` preserves today's cross-location behaviour (Cook, manual consume). | `Plantry.Inventory.Domain` (`ProductStock`) | A count is per-(product, **Location**) (C11), but `Consume` currently FEFO-orders across *all* active lots. Without this, a downward count in the Pantry could deduct a Garage lot. Minimal, backward-compatible. |
| **TS-9** | **Focused set-default-location command** in Catalog (`SetDefaultLocationCommand` over the existing `Product.SetDefaultLocation`). | `Plantry.Catalog.Application` | J5/J7 set a product's default location without rewriting its other fields. `UpdateProductCommand` exists but rewrites name/unit/category/expiry-defaults — too broad and clobber-prone for this. |

No change to `StockJournalEntry` (the schema CHECK already allows a `+/− Correction`), to FEFO ordering, to `xmin`/`FOR UPDATE`, or to depleted-lot retention.

---

## Count → domain-operation mapping

All operations run on the product's `ProductStock` root under the existing `FOR UPDATE` row lock; `source_type = Manual`, attributed to the counting user.

| Count action (per product, Location) | Domain operation |
|---|---|
| Counted **>** recorded (scalar) | `AddStock(delta, countedUnit, locationId, reason: Correction, expiry: null)` — a new opening-balance lot (TS-2, C8). |
| Counted **<** recorded (scalar, no reason) | `Consume(delta, countedUnit, reason: Correction, locationId)` — FEFO **within the Location** (TS-3). |
| Counted **<** recorded, "Used it" / "Spoiled" | same `Consume`, `reason: Consumed` / `Discarded` (C9). |
| Escape hatch — reduce a specific lot | `Consume(d, …, reason, targetEntry: lotId)` — `targetEntry` already scopes to one lot (no `locationId` needed). |
| Escape hatch — found more (with expiry) | `AddStock(d, …, reason: Correction, expiry: userExpiry)` — a **new lot**, never an in-place increase (TS-4). |
| Newly-added / never-stocked product | `AddStock(count, …, reason: Correction)` — the prior-recorded-= 0 case of the same opening-balance path. |

**TS-4 (upward is always a new lot).** There is deliberately **no** "increase an existing lot's quantity" operation. Reducing a specific lot is meaningful (it spoiled / was used → `Consume(targetEntry)`), but "found more" mints a fresh lot with its own (optional) expiry — you cannot assert the extra units belong to an existing batch. This keeps the aggregate to two write verbs (`AddStock`, `Consume`) and matches J3.

**TS-5 (delta unit).** The per-(product, Location) delta is computed in the user's **counted unit** (C10) via the product's `IQuantityConverter`: convert each in-Location lot's quantity to the counted unit, sum = recorded, `delta = counted − recorded`. The counted unit is then the journal `unit_id` — so provenance reads "user counted 3 *cans*," not a base-unit translation. (C10 guarantees a conversion exists for the chosen unit; otherwise the input falls back to the default/stored unit.)

---

## Application services (Inventory.Application)

| Service | Responsibility |
|---|---|
| **RecordCount** (per item) | Input: `(productId, locationId, countedValue, countedUnitId, reason, userId)`. Loads `ProductStock` `FOR UPDATE`, computes the delta against **current** stock (TS-5), dispatches `AddStock`(+) or `Consume`(−) or no-ops on zero. One aggregate, one transaction. The unit of work behind a single saved row. |
| **SaveCounts** (batch) | Orchestrates `RecordCount` over the changed working-set items. **N independent per-aggregate transactions**, not one global transaction (TS-6) — `ProductStock` is per-product, so a batch cannot be atomic across products. Returns a per-item result vector so the UI can report partial success/failure. |
| **AddItemDuringCount** | The inline-add path (J5): dedupe-search is a read; on create, calls the **write port** (TS-8) → Catalog `CreateProductCommand(trackStock: true, defaultLocationId: L)`, then `RecordCount` for the opening balance. |

**TS-6 (batch atomicity).** Each saved item commits on its own. Mirrors the existing `ConsumeStockCommand.ExecuteInTransactionAsync` pattern. Partial failure leaves earlier items committed (correct — each count is independent truth); the UI surfaces which rows failed for retry.

**TS-7 (idempotency via set-to-N).** Take Stock does **not** need the cook adapter's `sourceLineRef` token. Because every item's delta is recomputed against **current** stock at write time, re-driving a partially-failed `SaveCounts` naturally no-ops the already-applied items (their delta is now 0). Set-to-N recount semantics *are* the idempotency mechanism — for both the `AddStock` and `Consume` directions.

---

## Read models / queries

These are read-side and cross two contexts (Inventory lots + Catalog product metadata). They compose existing reads; **no new domain coupling**. Proposed home: a Take-Stock read facade assembled in `Plantry.Web` (or a dedicated read port) — to settle in the app-service pass.

| Query | Shape |
|---|---|
| **Location list** | The household's Catalog `location`s, plus a synthetic **"No location"** entry when the no-location query is non-empty (J1/J7). |
| **Location walk listing** (J2) | Per Location L: the **union** of (a) non-parent tracked products with active lots at L, and (b) non-parent tracked products whose `default_location = L` with no active lots at L — each with its **recorded count** (sum of active-lot quantities at L, in the product's default unit), default unit, and supported units (C5, C10). |
| **No-location listing** (J7) | Non-parent tracked, non-archived products with **no `default_location`** and **no active lots anywhere** (C5). |

Recorded count and lot detail (for the escape hatch) come from the Inventory side; name / default unit / default location / `track_stock` / parent-flag come from Catalog. RLS scopes both to the household.

---

## Cross-context ports

| Port | Direction | What's exchanged |
|---|---|---|
| **Inventory inline-add write port** (TS-8) | Inventory → Catalog | Defined in `Inventory.Application` (the analogue of Recipes' `ICatalogWriter`), implemented in `Plantry.Web` over Catalog's `CreateProductCommand` (`trackStock: true`) and `SetDefaultLocationCommand` (TS-9). All ids cross as raw `Guid` soft-refs. |
| **Catalog read** | Inventory → Catalog | Product name / default unit / default location / `track_stock` / parent flag / supported units, for the walk listings. By ID; no cross-schema FK (existing convention). |
| Existing `IQuantityConverter` | within Inventory | Unit conversion for the delta (TS-5) and for `Consume`'s lot-unit math (unchanged). |

---

## Key decisions (summary)

- **TS-1** No new aggregate / no session — Take Stock is journal `Correction`s on `ProductStock` (C7).
- **TS-2** Upward correction via a `reason`-parameterized `AddStock` + `IsAddition()` guard (C8).
- **TS-3** Location-scoped `Consume` via an optional `locationId` FEFO filter (C11).
- **TS-4** Upward is always a new lot; only downward has a per-lot (`targetEntry`) form.
- **TS-5** Delta computed and journaled in the user's counted unit (C10).
- **TS-6** Batch save = N per-aggregate transactions; partial success reported.
- **TS-7** Idempotency from set-to-N recompute; no `sourceLineRef`.
- **TS-8** Inline-add anti-corruption write port over Catalog commands (C12).
- **TS-9** Focused `SetDefaultLocationCommand` in Catalog.

---

## Open for later passes

- **Read-model placement** (TS-10): a dedicated `ITakeStockReader` port vs. inline Web composition of Catalog + Inventory reads — decide in the app-service pass.
- **Data-schema pass**: confirm no migration is needed beyond the (already-allowed) positive-`Correction` journal usage; verify the location-filtered FEFO has an appropriate index (`stock_entry` by `(household_id, product_id, location_id)` for the active-lot scan).
- **"Last counted" surface**: deferred; derivable from the newest `source_type = Manual` `Correction` row, no schema (per resolved D4).
