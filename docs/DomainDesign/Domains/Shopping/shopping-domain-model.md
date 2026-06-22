# Shopping — Domain Model

> **Status:** Implemented — Phase 1. Retrospective backfill; the DDD process was formalized after these contexts were designed.
>
> **Purpose:** The household's planning scratchpad — a mutable working list of items to buy. Unlike every other Phase-1 context, Shopping is deliberately **not** append-only: items are edited in place and hard-deleted on "Clear checked." It accepts additions from manual entry, Recipes ("add missing"), Meal Planning, and Deals.
>
> **Bounded context:** Shopping (`shopping` schema, Phase 1). References Catalog by ID for product names and category grouping; calls Pricing's read model for deal badges. Receives writes from Recipes and Meal Planning; never reads Inventory.
>
> **Code shape:** `ShoppingList` is the aggregate root (one per household in v1), `ShoppingListItem` its children. Category grouping and deal badges are read-time joins/calls — nothing is stored on the item.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregate map

| Aggregate root | Identity | Owns (composition) | Lifecycle |
|---|---|---|---|
| **ShoppingList** | `ShoppingListId` (UUIDv7) | `ShoppingListItem[]` | One per household in v1; created at household registration; never deleted |

`ShoppingListItem` is a mutable child: quantity, note, and check-off status are edited in place; checked items are **hard-deleted** on "Clear checked" (no soft-delete, no `archived_at`).

### v1 constraint

One `ShoppingList` per household. The root table and `name` column exist so multiple named lists are a **non-breaking future additive** — but only the single-list flow is built.

---

## Invariants

| # | Invariant | Enforced |
|---|---|---|
| **R1** | An item is **exactly one** of `product_id` (catalog product) or `free_text` (typed name) — never both, never neither | DB CHECK: `num_nonnulls(product_id, free_text) = 1` |
| **R2** | Category grouping is a **read-time join** to `catalog.product.category_id` — never stored on the item | Architecture |
| **R3** | Deal badge is a **read-time call** to Pricing's cheapest-active-deal read model — never stored on the item | Architecture |
| **R4** | Checked items are **hard-deleted** on "Clear checked" — no append-only, no soft-delete, no `archived_at` | App service |
| **R5** | **Check-off does not create stock** — `checked_at` is list state only; no Inventory coupling | Architecture |
| **R6** | Duplicate-product merge is in the **application layer**: bulk-add flows merge onto an existing unchecked item for the same product rather than inserting a duplicate; but a user can still manually add a second entry for the same product | App service |

---

## Writers

Multiple callers add items through a single `AddItems` application-service port, which applies the merge rule:

| Caller | `source` | `source_ref` |
|---|---|---|
| Manual UI | `manual` | null |
| Recipes "add missing" (J5) | `recipe` | `recipe_id` |
| Meal Planning "shop for this week" (Phase 3) | `meal_plan` | `meal_plan_id` |
| Deals stock-up (Phase 5) | `deal` | `deal_id` |

---

## Cross-context ports

| Direction | Context | What's exchanged |
|---|---|---|
| Reads (soft) | **Catalog** | Product name, `category_id` (for grouping read model); unit name |
| Reads (via service) | **Pricing** | Cheapest-active-deal read model for deal badge (Phase 5) — never direct table read |
| Receives writes | **Recipes** | `IShoppingListWriter.AddItems(product_id, scaledQty, unit_id, source="recipe", source_ref=recipeId)` |
| Receives writes | **Meal Planning** (Phase 2) | `IShoppingListWriter.AddItems(…, source="meal_plan")` |

Shopping **reads nothing from Inventory**. Check-off is list state; it does not signal a purchase.

---

## Key decisions

- **DM-18:** Mutable working state — not append-only, not soft-delete. Items are edited in place and hard-deleted on clear. This is the one Phase-1 context where in-place mutation and deletion are correct (it's a scratchpad, not a ledger).
- **Item is exactly one of product or free text (DM-18):** `num_nonnulls = 1` enforces the SPEC §3b "search for product **or** type free text" invariant at the DB.
- **Check-off does not create stock (DM-18):** After checking items the user goes to Intake (Add Stock) to log the real purchase. No phantom write path into Inventory.
- **Merge in app layer (DM-18):** No partial unique index — the app service merges bulk-add duplicates but allows intentional manual duplicates.
- **Single list, structurally extensible:** v1 builds one list but the root table and `name` column are there for the future.

> Full schema: [../DataModels/shopping.md](../../DataModels/shopping.md)
