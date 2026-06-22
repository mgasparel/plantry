# Shopping — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 1 (backfilled)
>
> **Purpose:** The shared vocabulary for the Shopping bounded context. Key terms — `ShoppingListItem`, `check off`, `clear checked`, `source` — appear in the ports exposed to Recipes and Meal Planning and in the cross-context merge rule.
>
> **Bounded context:** Shopping (`shopping` schema, Phase 1).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregates & Entities

| Term | Kind | Definition |
|---|---|---|
| **ShoppingList** | Aggregate root | The household's single planning list in v1. Contains all items to buy. One per household; created at registration. |
| **ShoppingListItem** | Entity (child of ShoppingList) | One line on the list. Either a catalog **product item** or a **free-text item** — never both. Mutable: quantity, note, and check status are edited in place. Hard-deleted on "Clear checked." |

---

## Key Terms

| Term | Definition |
|---|---|
| **Product item** | A `ShoppingListItem` backed by a `catalog.product` (`product_id` set, `free_text` null). Has quantity, unit, category grouping, and deal badge. |
| **Free-text item** | A `ShoppingListItem` with a typed name and no catalog product (`free_text` set, `product_id` null). Falls to the "Uncategorized" group; no deal badge. |
| **Check off** | Mark an item as obtained — sets `checked_at` (a timestamp, not a boolean) and records `checked_by` (attribution). Does **not** create Inventory stock. |
| **Uncheck** | Clear `checked_at` — move an item back to the active list. |
| **Clear checked** | Hard-delete all items where `checked_at IS NOT NULL`. The list's scratchpad clean-up action. |
| **Category grouping** | Read-time grouping of the list by `catalog.product.category_id`. Free-text items fall to "Uncategorized." **Not stored** on the item. |
| **Deal badge** | A read-time indicator on a product item showing the cheapest active deal (from Pricing's read model). **Not stored** on the item — a pure read-time join. Phase 5 display feature. |
| **`source`** | Provenance enum: `manual` / `recipe` / `meal_plan` / `deal`. Records what triggered the add. |
| **`source_ref`** | The UUID of the originating record (e.g., `recipe_id`, `meal_plan_id`) — completes the provenance for bulk-add flows. |
| **Merge** | When a bulk-add flow (Recipes, Meal Planning) adds a product already on the list as an unchecked item, the app-layer merge increments/updates it rather than inserting a duplicate. Intentional manual duplicates are still possible. |
| **`AddItems`** | The `IShoppingListWriter` port method called by Recipes and Meal Planning. The single surface through which external contexts add items. |
| **`checked_at`** | Timestamp (not boolean) on `ShoppingListItem`. Null = unchecked; set = checked. Enables ordering (unchecked first) and attribution (`checked_by`). |

---

## Key Actions

| Verb | Meaning |
|---|---|
| **Add item** | Add a product or free-text item to the list. Manual or via `IShoppingListWriter` from Recipes / Meal Planning / Deals. |
| **Edit item** | Update quantity, unit, or note in place. |
| **Check off** | Mark as obtained; sets `checked_at` + `checked_by`. Does not affect Inventory. |
| **Uncheck** | Restore an item to unchecked. |
| **Clear checked** | Hard-delete all checked items. |
| **Add missing** (Recipes) | Recipes' "add missing to shopping list" action — calls `AddItems` with `source="recipe"` for each `Missing` ingredient from the current `FulfillmentResult`. |

---

## Cross-context terms (referenced by ID, owned elsewhere)

| Term | Owned by | Notes |
|---|---|---|
| `product_id` | Catalog | For product items; drives category grouping and deal badge lookups |
| `unit_id` | Catalog | For product item quantity |
| `category_id` | Catalog | Read-time join for grouping (not stored on item) |
| `recipe_id` | Recipes | `source_ref` when `source = recipe` |
| `meal_plan_id` | Meal Planning | `source_ref` when `source = meal_plan` |
