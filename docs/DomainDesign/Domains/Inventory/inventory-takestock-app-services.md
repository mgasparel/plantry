# Take Stock — Application Services

> **Status:** Design in progress — Phase 4 (bd `plantry-5vxb`)
>
> **Purpose:** The concrete application surface — commands, read port, write port — that the UI calls, made specific enough to slice into tickets. Follows the existing Inventory conventions: constructor-injected commands returning `Result<T>` (see `StockCommands.cs`), persistence via `IProductStockRepository` (`FindForUpdateAsync` + `ExecuteInTransactionAsync`), and cross-context **ports defined in `*.Application`, implemented in `Plantry.Web`** (see `ICatalogReadFacade`, `IInventoryStockReader`, Recipes' `ICatalogWriter`). Builds on the [domain model](inventory-takestock-domain-model.md). Feeds the UI-slices pass.
>
> **Bounded context:** Inventory (`inventory` schema); Catalog reached only through ports.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model  →  Data Schema  →  App Services (← here)  →  UI Slices
```

---

## TS-10 resolved — a dedicated `ITakeStockReader`

The location-walk listing is Take-Stock-specific (per-Location **union** of lots-here ∪ defaulted-here; per-(product, Location) recorded counts; supported-unit lists). It is too specialized to bolt onto `IInventoryStockReader` (recipe-fulfillment shaped) or `ICatalogReadFacade` (reference-data shaped). 

**Decision:** a dedicated **`ITakeStockReader`** port in `Inventory.Application`, **implemented in `Plantry.Web`**. Its Web adapter composes the Inventory lot query (the `ix_stock_entry_by_location` scan, TS-S2) with **Catalog repositories read directly** — because the adapter lives in Web (which references Catalog), it needs *no* extension to `ICatalogReadFacade` to get a product's default location / supported units. Keeps the projection in one testable place and the Inventory project at `→ SharedKernel only`.

---

## Write commands (`Inventory.Application`)

### `RecordCountCommand` — the per-item unit of work

The set-to-N reconcile for one (product, Location). Loads the root `FOR UPDATE` inside a transaction, computes the delta against **current** stock, dispatches one direction.

```
RecordCountCommand(
    Guid productId, Guid locationId,
    decimal countedValue, Guid countedUnitId,
    StockReason reason,            // default Correction; Consumed/Discarded from the reason selector (C9)
    Guid userId,
    IProductStockRepository stocks, IProductConversionProvider conversions,
    IClock clock, ITenantContext tenant)
  → Result<RecordCountOutcome>     // (Direction Up|Down|NoOp, decimal AppliedDelta, decimal Shortfall)
```

Logic (inside `ExecuteInTransactionAsync`):
1. `stock = stocks.FindForUpdateAsync(household, productId)` — null ⇒ start a fresh root (first-ever stock; handles the never-stocked / J5 case).
2. `recorded` = sum of **active lots at `locationId`**, each converted to `countedUnitId` via the product converter (TS-5).
3. `delta = countedValue − recorded`.
   - `delta > 0` → `stock.AddStock(delta, countedUnitId, locationId, …, reason: Correction)` (TS-2).
   - `delta < 0` → `stock.Consume(−delta, countedUnitId, reason, converter, …, locationId)` (TS-3).
   - `delta == 0` → no-op (no journal row).
4. Save; return the outcome.

Idempotent by construction (TS-7): a re-drive recomputes `recorded`, so an already-applied item yields `delta == 0`.

### `SaveCountsCommand` — the batch Save (J4)

```
SaveCountsCommand(IReadOnlyList<CountItem> items, Guid userId, …deps…)
  → Result<IReadOnlyList<CountItemResult>>
```

- `CountItem` is **either** a scalar (`ProductId, LocationId, CountedValue, CountedUnitId, Reason`) **or** an escape-hatch payload (per-lot `LotAdjustment(EntryId, NewQuantity, Reason)[]` + `FoundLot(Quantity, UnitId, Expiry, Reason)[]`).
- Runs **one independent transaction per item** (TS-6) — `RecordCountCommand` for scalars; for escape-hatch items, the same load-`FOR UPDATE` wrapper calling `Consume(targetEntry:…)` per reduced lot and `AddStock(reason:Correction, expiry:…)` per found lot.
- Returns a per-item result vector so the UI reports partial success/failure. No global rollback — each saved item is independent truth.

### Inline add (J5) — `AddCountedItemCommand`

Composes the write port + a count:
1. Dedupe is a **read** (`ITakeStockReader` search) before this command.
2. On create: `writer.CreateTrackedProductAsync(name, defaultUnitId, categoryId?, defaultLocationId: L)` (TS-8) → new `productId`.
3. `RecordCountCommand(productId, L, count, unit, reason: Correction, …)` for the opening balance (C8).

---

## Write port — `ITakeStockCatalogWriter` (TS-8)

Defined in `Inventory.Application`, implemented in `Plantry.Web` over Catalog commands — the exact analogue of Recipes' `ICatalogWriter`/`CatalogWriterAdapter`:

```
interface ITakeStockCatalogWriter {
    Task<Guid> CreateTrackedProductAsync(           // over CreateProductCommand(trackStock: true)
        string name, Guid defaultUnitId, Guid? categoryId, Guid defaultLocationId, CancellationToken ct);
    Task SetDefaultLocationAsync(                    // over SetDefaultLocationCommand (TS-9)
        Guid productId, Guid locationId, CancellationToken ct);
}
```

- **`CreateTrackedProductAsync`** differs from Recipes' `CreateUntrackedStapleAsync` only in `trackStock: true` and passing a `defaultLocationId` (C12). Dedup is Catalog's existing `FindByNameAsync` (throws on duplicate; the search-first step makes it rare).
- **`SetDefaultLocationAsync`** powers J7 (file an unassigned product) and is also how a 0-count "file this product" cleanup works.
- **Requires the new Catalog `SetDefaultLocationCommand`** (TS-9) — focused write over `Product.SetDefaultLocation`, not the field-clobbering `UpdateProductCommand`.

---

## Read port — `ITakeStockReader` (TS-10)

Defined in `Inventory.Application`, implemented in `Plantry.Web`:

```
interface ITakeStockReader {
    Task<IReadOnlyList<TakeStockLocation>> ListLocationsAsync(CancellationToken ct);              // J1
    Task<IReadOnlyList<TakeStockRow>>      ListLocationRowsAsync(Guid locationId, CancellationToken ct); // J2
    Task<IReadOnlyList<TakeStockRow>>      ListNoLocationRowsAsync(CancellationToken ct);          // J7
    Task<IReadOnlyList<TakeStockLot>>      ListLotsAsync(Guid productId, Guid locationId, CancellationToken ct); // J3
    Task<IReadOnlyList<ProductSearchHit>>  SearchProductsAsync(string term, CancellationToken ct); // J5 (exact/contains; fuzzy = plantry-hl4a)
}

record TakeStockLocation(Guid? LocationId, string Name, int ItemCount, bool IsNoLocationBucket);
record TakeStockRow(Guid ProductId, string ProductName, decimal RecordedCount,
                    Guid DefaultUnitId, string DefaultUnitCode,
                    IReadOnlyList<UnitOption> SupportedUnits, int LotCount, bool NeverStockedHere);
record TakeStockLot(Guid EntryId, decimal Quantity, Guid UnitId, string UnitCode,
                    DateOnly? Expiry, bool IsOpen, bool IsFrozen);
```

- **`ListLocationRowsAsync`** is the union (C5): non-parent tracked products with active lots at L **∪** non-parent tracked products with `default_location = L` and none here (recorded 0, `NeverStockedHere = true`). Served by the `ix_stock_entry_by_location` scan + a Catalog product read.
- **`SupportedUnits`** comes from the product's conversions (Catalog) — drives the C10 unit selector.

---

## Wiring / project placement

| Piece | Project |
|---|---|
| `RecordCountCommand`, `SaveCountsCommand`, `AddCountedItemCommand` | `Plantry.Inventory.Application` |
| `ITakeStockCatalogWriter`, `ITakeStockReader` (interfaces + DTOs) | `Plantry.Inventory.Application` |
| `TakeStockCatalogWriter`, `TakeStockReader` (adapters) | `Plantry.Web` (over Catalog commands/repos + Inventory query) |
| `SetDefaultLocationCommand` | `Plantry.Catalog.Application` |
| `AddStock`/`Consume`/`StockReason` changes (TS-2/TS-3) | `Plantry.Inventory.Domain` |
| Page(s) + handlers (route TBD, e.g. `/pantry/take-stock`) | `Plantry.Web/Pages` — UI-slices pass |

---

## Proposed slice plan (for ticket-splitting)

Ordered by dependency; each is independently shippable and testable. Sizing is rough.

1. **Domain primitives** — TS-2 (`AddStock` reason + `IsAddition`), TS-3 (`Consume` `locationId`). Pure domain + unit tests. *Foundational; everything depends on it.*
2. **Catalog `SetDefaultLocationCommand`** (TS-9). Small, standalone.
3. **`ITakeStockReader` + adapter + the `ix_stock_entry_by_location` migration** (TS-S2). The read side: location list, location-walk union listing, lot detail. *Depends on nothing in this list (read-only).* 
4. **`RecordCountCommand` + `SaveCountsCommand`** (scalar path) and the count page wired to read (3) + write. The core walk-and-save loop (J1/J2/J4). *Depends on 1, 3.*
5. **Escape hatch** (J3) — per-lot adjustments in `SaveCountsCommand` + UI. *Depends on 4.*
6. **Shared product-search/create sheet extraction** (C12) — extract from `Pages/Recipes/Edit.cshtml` into a shared component; behaviour-preserving for Recipes. *Foundational for 7; touches a hotspot — keep recipe flow green.*
7. **Inline add** (J5) — `ITakeStockCatalogWriter` + `AddCountedItemCommand` + consume the shared sheet (6). *Depends on 1, 2, 4, 6.*
8. **"No location" section** (J7) — `ListNoLocationRowsAsync` + assign-location-on-count + `SetDefaultLocationAsync`. *Depends on 2, 4.*
9. **Onboarding entry from Today** (J6) — Home (Today) cold-start CTA into Take Stock. *Cross-context (Home); depends on 4 existing.*

---

## Idempotency / transactions (recap)

- Every write loads the root `FOR UPDATE` inside `ExecuteInTransactionAsync`; `xmin` is the optimistic backstop (DM-13).
- Batch Save = **N per-aggregate transactions** (TS-6); partial success is reported, never a global rollback.
- Re-drive safety is **set-to-N recompute** (TS-7), not a `sourceLineRef` token.

---

## Open for the UI-slices pass

- Page/route shape and how htmx/Alpine model the per-Location working set (C7 — page-only, "leave page?" guard on unsaved counts).
- The reason selector, lot escape hatch, unit selector, and the shared add-item sheet as concrete components (reuse-first per the component-library rule).
