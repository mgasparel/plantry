# Take Stock — Ubiquitous Language

> **Status:** Design in progress — Phase 2 (bd `plantry-5vxb`)
>
> **Purpose:** The shared vocabulary for the **Take Stock** feature within the Inventory context. Take Stock introduces almost no new domain mechanics — it is a batch UI over existing Inventory operations — so this doc mostly **maps friendly count language onto the existing journal taxonomy** and names the few genuinely new UI/flow concepts. Builds on [inventory-ubiquitous-language.md](inventory-ubiquitous-language.md); feeds the domain-model and data-schema passes.
>
> **Bounded context:** Inventory (`inventory` schema). Reads Catalog for Locations, products, units; writes Catalog only via the inline-add port (C12).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## New terms (Take Stock)

| Term | Kind | Definition |
|---|---|---|
| **Take Stock** | Feature / action | The flow of walking storage Locations and reconciling recorded stock against what is physically on hand. The page title and primary CTA. |
| **Count** | Verb & noun | The user's act of entering the *actual* on-hand quantity for a (product, Location); also that entered number. The user-facing word for the whole activity — surfaces read "count" / "counted". |
| **Recorded count** | Term | What Plantry currently believes is on hand for a (product, Location): the sum of that product's **live lot** quantities at that Location. The baseline a count is compared against; pre-fills the input. |
| **Counted value** (a.k.a. *actual count*) | Term | The authoritative number the user enters — "there are N, period." On Save it wins as a set-to-N recount (C6). |
| **Reconcile** | Verb | Bring recorded stock in line with the counted value by writing the signed delta as journal entries. Take Stock reconciles; it does not invent a new movement type. |
| **Opening balance** | Term | Initial stock established for a product that had **none** at a Location — found stock, or a newly-added / never-inventoried product. Written as an **upward `Correction`** (positive delta), explicitly **not** a `Purchase`, so spend/Pricing data stays clean (C8). |
| **Working set** | Term | The page-only collection of pending counts not yet saved. Has no server persistence and **no draft/resume** — discarded if the user leaves before Save (C7). |
| **Save** | Verb | Commit the working set: for each changed item write the reconciling journal entries under the `ProductStock` row lock. The only durable step (C7, J4). |
| **Reason selector** | UI control | The per-item control that sets the *why* on a downward delta — **Used it** / **Spoiled** / (default) **unknown** — mapping to `Consumed` / `Discarded` / `Correction` (C9). |
| **Lot escape hatch** | UI control | Expanding a product row to adjust **individual lots** (quantity, expiry, per-lot reason) instead of a single scalar (C2, J3). |
| **Inline add** ("+ Add item") | Action | Search-first add of an existing or newly-created product during a walk, reusing the Recipes product-search/create sheet; new products are created `trackStock: true` (C12, J5). |
| **"No location" group** | Term | The surfaced bucket of tracked products that have **neither lots nor a default location** (typically an import artifact); counted by assigning a location inline (C5, J7). |

---

## Reused vocabulary (from Inventory UL — unchanged)

Take Stock writes **only** existing journal concepts. These keep their established meanings:

| Term | How Take Stock uses it |
|---|---|
| **`Correction`** (Reason) | The default reconciling delta — both downward (unknown shrinkage) and **upward** (found stock / opening balance). |
| **`Consumed`** (Reason) | A downward delta the user attributes to real use ("Used it") — keeps consumption analytics truthful. |
| **`Discarded`** (Reason) | A downward delta the user attributes to waste ("Spoiled / threw out"). |
| **`Purchase`** (Reason) | **Never** written by Take Stock — a recount is not a purchase (the whole point of the C8 opening-balance `Correction`). |
| **Source type = `Manual`** | Every Take Stock journal row is `Manual`-sourced, attributed to the counting user. |
| **Lot / `StockEntry`** | The unit a count resolves to; scalar counts place deltas FEFO across lots, the escape hatch places them per-lot. |
| **FEFO** | The default lot-placement order for a downward scalar delta (earliest-expiry first). |
| **Delta** | The signed difference (counted − current), in the lot's unit, that each journal row records. |
| **`ProductStock`** | The aggregate root locked per product at Save; the concurrency anchor (DM-13). |
| **Depleted lot** | A lot a count drives to 0 — retained with `depleted_at` (R4); its product then resurfaces at recorded 0 in its default Location next time. |

---

## UI term ↔ domain term mapping

The single most important table — it stops the friendly "count" language from drifting away from the journal taxonomy:

| What the user does | What is written |
|---|---|
| Counts **fewer** than recorded, no reason given | `Consume` with `reason = Correction` (FEFO across lots) |
| Counts fewer, taps **"Used it"** | `Consume` with `reason = Consumed` |
| Counts fewer, taps **"Spoiled / threw out"** | `Consume` with `reason = Discarded` |
| Counts **more** than recorded | Upward **`Correction`** — new lot (opening balance), expiry optional |
| Counts a **never-stocked / newly-added** product | Upward **`Correction`** opening balance (the prior-recorded-= 0 case) |
| **Adds** a brand-new product inline | Catalog product create (`trackStock: true`) **+** the opening-balance `Correction` |
| Counts an item in **"No location"** | Opening-balance `Correction` in the chosen Location **+** Catalog default-location set |
| Leaves without **Save** | Nothing — the working set is discarded (C7) |

---

## Naming guardrails

- **"Count" / "counted"** is the user-facing language for the activity (decided with user).
- **"Recorded count" vs "counted value"** — keep these distinct; the first is the system's belief, the second is the user's truth. The delta between them is what gets written.
- **"Opening balance"** is an *internal/design* term for the upward-`Correction` initial-stock case; it need not appear in the UI (open: whether it earns a place in the shared spoken vocabulary or stays an implementation note).
