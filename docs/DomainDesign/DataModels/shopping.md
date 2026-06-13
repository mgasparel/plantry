# Context 6 — Shopping (`shopping` schema) ✅

The only **mutable working-state** context in Phase 1. Unlike the journal and `price_observation` tables (append-only, DM-4) or Catalog reference data (soft-delete), shopping items are a scratchpad: edited in place, and **hard-deleted** on "Clear checked" (SPEC §3e). No `archived_at`, no append-only discipline.

`ShoppingList` is the aggregate root, `shopping_list_item` its children (ADR-010). **Category grouping** and **deal badges** are read-time joins, never stored on the item.

---

**`shopping_list`** — aggregate root; one row per household in v1

| Column | Type | Notes |
|---|---|---|
| `shopping_list_id` | `uuid` PK | UUIDv7 |
| `household_id` | `uuid` | tenancy (RLS) |
| `name` | `text` | defaults to `"Shopping List"`; v1 seeds **one** list per household. The column + root table exist so multiple named lists are a non-breaking future change — **not** built in v1 |
| `created_at` / `updated_at` | `timestamptz` | |

`UNIQUE (household_id, shopping_list_id)` — tenant-safe child-FK anchor (conventions: children carry the composite FK).

---

**`shopping_list_item`** — child of `ShoppingList`

| Column | Type | Notes |
|---|---|---|
| `shopping_list_item_id` | `uuid` PK | UUIDv7 |
| `household_id`, `shopping_list_id` | `uuid` | composite **FK → `shopping_list`** (within-context, enforced) |
| `product_id` | `uuid` null | soft ref → `catalog.product` |
| `free_text` | `text` null | for non-catalog items (SPEC §3b "or type free text") |
| `quantity` | `numeric(12,3)` null | optional (SPEC §3b "optionally set quantity") |
| `unit_id` | `uuid` null | soft ref → `catalog.unit` |
| `note` | `text` null | per-item note (SPEC §3) |
| `checked_at` | `timestamptz` null | **null = unchecked**; drives checked-to-bottom ordering and "clear checked" (SPEC §3c/§3e) |
| `checked_by` | `uuid` null | attribution, soft ref → identity user (multi-member households) |
| `source` | `text` | `manual` / `recipe` / `meal_plan` / `deal` (CHECK), default `manual` — provenance for the bulk-add flows (SPEC §3d, §5d, §6c). `deal` is modeled forward-looking; its writer (Deals stock-up, §6c) is Phase 3 |
| `source_ref` | `uuid` null | soft ref to the originating `recipe` / `meal_plan` / `deal`; mirrors `price_observation.source_ref` |
| `created_at` / `updated_at` | `timestamptz` | **mutable** — fields are edited in place, rows deleted on clear |

`CHECK (num_nonnulls(product_id, free_text) = 1)` — an item is **exactly one** of a catalog product or free text, never both, never neither.

Backing index `(household_id, shopping_list_id)` for the list view; `checked_at` participates in the default sort (unchecked first, then `created_at`).

---

## Read models (not tables / not stored)

- **Category grouping** (SPEC §3a) — join `product_id → catalog.product.category_id` at read time. Free-text items (no `product_id`) fall to an **"Uncategorized"** bucket. No `category_id` is stored on the item.
- **Deal badge** (SPEC §3f, Phase 4) — call Pricing's "cheapest active deal" read model by `product_id`. Shopping never reads Deals/Pricing tables directly (ADR-010, no cross-context table reads) and never stores the badge on the item.

---

## Resolved calls ✅

1. **Single list per household, structurally extensible.** v1 seeds and uses one `shopping_list` per household — SPEC only ever references "the Shopping tab." The root table and `name` column still exist, so multiple named lists later is additive, not a migration. *Rejected for v1:* building list create/rename/switch UX that SPEC doesn't describe.

2. **Mutable working state — not append-only, not soft-delete.** Shopping is a scratchpad, distinct from the journal/price log (append-only) and Catalog (soft-delete). Items are edited in place and **hard-deleted** on "Clear checked." No `archived_at`. This is the one Phase-1 context where in-place mutation and deletion are correct.

3. **Item is exactly one of product or free text.** `num_nonnulls(product_id, free_text) = 1` enforces SPEC §3b's "search for product **or** type free text" at the DB. Free-text items carry no category (→ "Uncategorized" group); a `category_id` on the item was **rejected** as a stored field that's null for most rows and duplicates a pure read-time concern.

4. **Provenance via `source` + `source_ref`.** The bulk-add flows — recipe "add missing" (§3d/§4), meal-plan "shop for this week" (§5d), deal stock-up (§6c) — stamp where each item came from. This mirrors `price_observation.source_ref` and powers the app-layer de-dup below.

5. **Duplicate products merge in the application layer, no DB constraint.** When a bulk-add targets a product already on the list as an unchecked item, the add-item service **merges** (increments/updates) rather than inserting. No partial unique index — so the user can still *intentionally* add a second manual entry for the same product. *Rejected:* a `UNIQUE (shopping_list_id, product_id) WHERE checked_at IS NULL` index (blocks intentional dupes) and unconstrained inserts (lets "add missing" pile up repeats).

6. **Check-off does not create stock.** Per SPEC §3c, after checking items the user goes to Add Stock (Intake) to log the real purchase. There is **no coupling** between Shopping check-off and Inventory — `checked_at` is list state only. This keeps Shopping a pure planning surface and avoids a phantom write path into the journal.

---

> **Writers.** Manual add (UI, `source = manual`); Recipes "add missing to shopping list" (§3d, `source = recipe`, `source_ref = recipe_id`); Meal Planning "shop for this week" (§5d, `source = meal_plan`); Deals stock-up "add to list" (§6c, Phase 3+, `source = deal`). All go through Shopping's add-item application service, which applies the §5 merge rule.

> **Readers.** The list view composes `shopping_list_item × catalog.product` (name, category) `× catalog.unit`, and — Phase 4 — Pricing's cheapest-active-deal read model for the badge. All via application services / read models, never direct table reads (ADR-010).

> **ADR note.** Confirms ADR-010 §Aggregates "Shopping — `ShoppingList` root with `Item` children (`product_id` or free text, qty, note, checked); deal badges are a read-time join, never stored." Refinements added by this model: `checked` is a `checked_at` **timestamp** (not a boolean) for ordering + attribution; `source`/`source_ref` provenance; and the explicit mutable / hard-delete lifecycle.
