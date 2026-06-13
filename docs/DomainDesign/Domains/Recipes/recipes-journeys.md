# Recipes — User Journey Map

> **Status:** Design in progress — Phase 2
>
> **Purpose:** Checkpoint of user journey mapping session. Feeds into ubiquitous language, domain model, and data schema (next steps). Raw notes in `RecipesUserJourneys.md` at project root.

---

## DDD Process

```
User Journeys (← here)  →  Ubiquitous Language  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Confirmed Decisions

| # | Decision | Outcome |
|---|----------|---------|
| C1 | Browse view mode preference | `localStorage` (client-side only; no server round-trip) |
| C2 | Tags | **Purpose is meal-planner input first**, browse-filter second. A **controlled, shared household vocabulary referenced by ID** (not free-text), so recipe tags and user preferences resolve to the same token. **Kind-less** — hard/soft force is not on the tag; it lives as a per-member **Stance** (Required · Preferred · Neutral · Disliked · Restricted) on the User↔Tag edge (future `UserPreference`, see FUTURE.md). Optional **cosmetic category** (Diet/Protein/Flavor/Cuisine) for UI grouping only. **8 defaults** seeded at household creation: Vegetarian, Vegan, Dairy-Free, Gluten-Free (Diet); Meat, Poultry, Fish (Protein); Spicy (Flavor). New tags creatable inline (J6). Recipe↔Tag is plain membership. See `recipes-ubiquitous-language.md`. |
| C3 | Cook history | Dedicated `cook_event` table in `recipes` schema (`recipe_id`, `servings_cooked`, `cooked_at`). Cheaper and cleaner than reconstructing from stock journal entries; enables future history / frequency features. |
| C4 | Duplicate recipe names | Not allowed. `UNIQUE(household_id, name)` constraint. |
| C5 | Source field | Free-text nullable field on the Recipe aggregate (cookbook name, URL, or any reference). |
| C6 | Named ingredient sections | `group_heading` nullable field on Ingredient — already in ADR-010. |
| C7 | Variant disambiguation | Auto-select best variant (highest stock / FEFO order); user can override before confirming. |
| C8 | Consume when stock is low or insufficient | No blocking. Consume whatever quantity is available; proceed with the cook. |
| C9 | Ingredient editing at cook time | Full CRUD at cook time regardless of stock status. Minimum friction principle: the user can swap, skip, or modify any ingredient (not just missing ones) before confirming the cook. |
| C10 | Unit mismatch at recipe authoring | Block on save if no conversion path exists. Allow user to specify the `ProductConversion` **inline** in the recipe editor rather than navigating to the Catalog. Conversion written to Catalog on save. |
| C11 | Unit-incompatible variants in disambiguation picker | Show all variants in the picker; disable (grey out) incompatible ones with a label (e.g., "No unit conversion available"). Not hidden — user can see their stock exists — but not selectable. No inline conversion at cook time. Rationale: C10 makes this nearly impossible in practice; when it does occur the fix belongs in the recipe or Catalog, not mid-cook. |
| C13 | Directions format | **Paragraph-delimited auto-numbered steps** (the Paprika/Mela convergent pattern). Stored as a single `directions` text field; each paragraph/newline = one step, **auto-numbered at render** (styled numbered circles). A `#` heading starts a section and **resets numbering** (e.g. "For the sauce"). Authoring needs no Markdown knowledge — Enter = next step; an optional formatting toolbar *emits* the light inline syntax (bold/italic/heading). Ingredient names auto-highlighted at render by matching the recipe's ingredient list (convention-free). **Cook-mode-ready for free:** steps are *derived* by splitting on render, so a future guided cook mode needs no schema change; per-step timers/durations would later migrate to `recipe_step` child rows — paid for only when that feature is real. Chosen over raw Markdown (non-technical authors) and rigid structured-step tables (kills one-column simplicity + easy URL import) — captures B's upside without its rigidity. |
| C12 | Free-text / unmapped ingredients | **No free-text.** Every recipe ingredient resolves to a Catalog `product` (`pid` is never null). To keep authoring friction low, an unmatched ingredient name auto-creates an **untracked staple** product (`track_stock = false`) inline — search Catalog → on no match, one tap creates the product, warns the user, and links it. Driven by VISION Pillar 1 (pantry as ground truth) + Pillar 2 (inline creation without breaking the flow). Untracked products are catalogued and costable (can carry SKUs / price history) but exempt from quantity accounting. Behaviour: fulfillment treats them as **always satisfied** (never Missing); cook **skips** consume; never auto-added to shopping list. Auto-created stubs have no price history, so they contribute nothing to cost-per-serving (existing J3 exclusion). Users can enable tracking later from the product page. Adds `track_stock` to `catalog.product` — see `DataModels/catalog.md`. |

---

## Open Decisions (Deep Dives Needed)

| # | Decision | Context |
|---|----------|---------|
| ~~D1~~ | ~~Unit-incompatible variants in disambiguation picker~~ | Resolved → C11 |
| ~~D2~~ | ~~Free-text / `pid = null` ingredients~~ | Resolved → C12 |
| ~~D3~~ | ~~Directions format~~ | Resolved → C13 |

---

## Journeys

### J1 — Browse by pantry availability

**Trigger:** User navigates to the Recipes section.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Loads all household recipes. Computes `FulfillmentResult` (% ingredients in pantry) and `CostPerServing` (from price history) for each. |
| 2 | System | Default sort: Fulfillment descending — most cookable recipes first. |
| 3 | System | Flags recipes where ≥1 ingredient expires within 4 days with a "Use soon" indicator. |
| 4 | User | Optionally toggles the "Use soon" filter → list narrows to recipes with at-risk ingredients. |

**Domain events emitted:** none (pure query)

**Edge cases:**
- No recipes exist → empty state, prompt to create first recipe
- Ingredient references a product not in the household Catalog → treated as `Missing`
- Ingredient references a parent product (DM-19) → fulfillment sums stock across all variant children

---

### J2 — Browse by tag / search / sort

**Trigger:** User interacts with search, tag filters, or sort controls on the Browse page.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | User | Types in search field → list filters live to recipes whose name contains the text (case-insensitive). |
| 2 | User | Clicks a tag pill → list filters to recipes with that tag; clicking the active tag deselects it. |
| 3 | User | Selects a sort criterion (Fulfillment, Cost, Cook time, Name, Recently added) → list reorders; second click on same criterion reverses direction. |
| 4 | System | All active filters (search + tag + "Use soon") combine as AND logic. |
| 5 | User | Switches between Gallery view (visual cards) and Grid view (table). |
| 6 | System | Saves view mode preference to `localStorage`; restores on next visit. |

**Domain events emitted:** none

**Edge cases:**
- No recipes match active filters → empty filtered state with visible active filters (user can clear them)

---

### J3 — Inspect a recipe

**Trigger:** User clicks a recipe card or grid row.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Loads Recipe aggregate: name, source, tags, photo, cook time, default servings, directions, ingredient list. |
| 2 | System | Computes `FulfillmentResult` fresh from Inventory: per-ingredient status (`InStock` / `Low` / `Missing`); flags any ingredient expiring within 4 days. |
| 3 | System | Computes `CostPerServing` from `PriceObservation` data. Ingredients with no price history are excluded from cost (shown as partial/approximate). |
| 4 | System | Renders: hero image, meta bar (cook time, servings, cost), fulfillment card, ingredient list with per-ingredient status, directions. |
| 5 | User | Adjusts servings via stepper → ingredient quantities scale proportionally. Client-side only (Alpine.js), no server round-trip after initial load. |
| 6 | User | Taps "Add X missing to shopping list" → J5 |
| 7 | User | Taps "Cook this" → J4 |
| 8 | User | Taps "Edit recipe" → J7 |

**Domain events emitted:** none

**Edge cases:**
- No photo → placeholder renders
- Ingredient references parent product with no variants in stock → `Missing`
- All ingredients `InStock` → fulfillment card shows "You have everything"; "Add missing" button disabled
- Untracked staple (`track_stock = false`) → counts as satisfied in fulfillment, shown with a distinct "staple" indicator (no stock pip); excluded from cost if no price history (C12)
- No price data at all for recipe → `CostPerServing` omitted (not shown as zero)

---

### J4 — Cook a recipe

**Trigger:** User taps "Cook this" on the Detail page (J3, step 7).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Opens Cook confirmation screen. Scales all ingredient quantities by `(desired_servings / recipe.default_servings)`. |
| 2 | System | Resolves each ingredient: non-parent product → direct; parent product → auto-selects best variant (highest stock / FEFO). User can override selection. |
| 3 | System | For parent-product ingredients, surfaces the **Variant Disambiguation Picker**: shows all variants. Unit-compatible variants are selectable. Unit-incompatible variants are visible but disabled with a label ("No unit conversion available") — not hidden, so the user knows the stock exists (C11). User may split required quantity across multiple compatible variants. If no variants → treated as `Missing`. |
| 4 | User | Has full CRUD over the ingredient list at this point (minimum friction principle). Can: swap any ingredient for another product, skip any ingredient, change quantities, add unlisted items. No stock status blocks editing. |
| 5 | System | If stock is insufficient for a selected quantity, consumes whatever is available and proceeds. No blocking (C8). |
| 6 | User | Reviews the final plan and confirms. |
| 7 | System | For each tracked ingredient (not skipped), calls `ProductStock.Consume(quantity, unit, reason="Recipe", sourceRef=recipeId)` (ADR-011). Inventory handles FEFO ordering (DM-13). Untracked staples (`track_stock = false`) are skipped — no consume (C12). |
| 8 | System | Writes a `cook_event` row: `recipe_id`, `servings_cooked`, `cooked_at` (C3). |
| 9 | System | Returns user to Detail page; `FulfillmentResult` re-computed (stock has changed). |

**Domain events emitted:** `RecipeCooked(recipeId, householdId, servingsCooked, cookedBy, at)` (O2)

**Edge cases:**
- All ingredients `Missing` → warning shown, cook allowed; user decides
- Ingredient `Low` but available quantity ≥ required → consume full required amount, no warning needed
- Ingredient `Low` and available quantity < required → consume available amount, no blocking (C8)
- Concurrent cook by two household members → Inventory handles via `xmin` + `FOR UPDATE` (DM-13); second cook sees reduced availability

---

### J5 — Add missing ingredients to shopping list

**Trigger:** User taps "Add X missing to shopping list" on the Detail page (J3, step 6).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Identifies all Ingredients with status `Missing`, scaled to the current servings count displayed on page. |
| 2 | System | Calls `Shopping.AddItems()` for each: `product_id`, scaled `quantity`, `unit_id`, `source="recipe"`, `source_ref=recipeId`. |
| 3 | System | Per DM-18: quantities merge if product already on the shopping list (no duplicates). |
| 4 | System | Button changes to "Added to shopping list ✓" for the remainder of the session. |

**Domain events emitted:** none (Shopping context owns its state)

**Edge cases:**
- All ingredients `InStock` → button disabled; nothing to add
- User adjusts servings after adding → button resets to "Add X missing" (quantity context changed)
- Untracked staple (`track_stock = false`) → always satisfied, never `Missing`; excluded from "add missing" entirely (C12)

---

### J6 — Create a recipe

**Trigger:** User taps "New recipe" from the Browse page or global nav.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Opens blank Create form. |
| 2 | User | Fills in: **Name** (required), **Servings** (required, ≥ 1), Cook time (optional, minutes), Source (optional free text — cookbook, URL, etc.), Photo (optional upload), Tags (multi-select from household tag list; can create a new tag inline). |
| 3 | User | Authors Directions in a plain editor: Enter starts the next step (each paragraph auto-numbers on render); a section heading resets numbering ("For the sauce"). Optional formatting toolbar for bold/italic/heading — no Markdown knowledge required (C13). |
| 4 | User | Adds ingredients one by one: search Catalog by product name → select result → enter quantity + unit → optionally add a group heading before this ingredient (e.g., "Salad", "Dressing"). |
| 4a | System | If the typed name matches no Catalog product, offers "Create '_name_' as an untracked staple" (C12). One tap mints a `product` with `track_stock = false`, `default_unit_id` = the unit entered on this line (or household `ea` if none), warns the user inline, and links it. Quantity/unit may be left blank for untracked ingredients ("to taste"). |
| 5 | System | If selected unit has no conversion path to the product's unit, blocks save and presents inline `ProductConversion` form (C10). User enters the conversion; it is written to Catalog on recipe save. _(Untracked staples skip this — no quantity accounting.)_ |
| 6 | User | Reorders ingredients (drag-and-drop or up/down controls); deletes ingredients. |
| 7 | User | Saves. |
| 8 | System | Validates: name required and unique within household (C4), servings ≥ 1, at least 1 ingredient. |
| 9 | System | Persists `Recipe` aggregate. |
| 10 | System | Navigates user to the new recipe's Detail page. |

**Domain events emitted:** `RecipeCreated(recipeId, householdId, at)`

**Edge cases:**
- Duplicate name → validation error: "A recipe with this name already exists" (C4)
- Photo upload fails → recipe saves without photo; non-blocking notification to user
- User navigates away mid-form → standard browser "Leave page?" guard

---

### J7 — Edit a recipe

**Trigger:** User taps "Edit recipe" on the Detail page (J3, step 8).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Opens Edit form pre-populated with all current Recipe data. |
| 2 | User | Can modify any field: name, servings, cook time, source, photo (remove or replace), tags, directions, ingredient list. |
| 3 | User | Changes **default servings** → system offers to proportionally scale all ingredient quantities. User can accept or decline. |
| 4 | User | Ingredient edits: add, remove, reorder, change quantity / unit / group heading. |
| 5 | System | Same unit-mismatch validation as Create (C10): blocks save if unresolvable; offers inline `ProductConversion` form. |
| 6 | User | Saves. |
| 7 | System | Validates (same rules as Create). Ingredient collection fully replaced on save (new ordered list is authoritative). |
| 8 | System | Returns user to Detail page showing updated recipe. |

**Domain events emitted:** `RecipeUpdated(recipeId, householdId, at)`

**Edge cases:**
- Renaming to an existing name → validation error (C4)
- Removing an ingredient that was previously added to the shopping list → shopping list is **not** automatically updated (user manages shopping list independently)
- Changing `default_servings` and accepting the scale offer → stored ingredient quantities update proportionally; future fulfillment reads remain correct
- Changing `default_servings` and declining the scale offer → stored quantities unchanged; scaling ratio shifts at cook time (user's explicit choice)

---

## Cross-Cutting Notes

**Minimum friction principle (from J4 notes):** The cook flow is not a gating mechanism. Users can cook a risotto without white wine in stock; users can iterate on a recipe while tracking inventory usage. The system records what actually happened, not what it expected to happen.

**Inline `ProductConversion` (C10):** When authoring a recipe reveals a unit gap — "this recipe needs 1 cup of flour, but flour is catalogued in grams" — the editor surfaces a small inline form to enter the conversion factor. This writes a `ProductConversion` child to the `Product` aggregate in Catalog. It avoids the context-switch to Catalog → Product → Edit → Add Conversion. The conversion persists beyond the recipe and benefits all future recipes using the same product.

**`FulfillmentResult` is always fresh:** It is never cached on the `Recipe` aggregate. It is computed at query time from live `ProductStock` data. This ensures the Browse view always reflects actual pantry state.

**Directions storage & rendering (C13):** Stored as a single `directions` text field on the `Recipe` aggregate (the recipes schema is TBD — see the domain-model pass). Steps are **not** persisted as discrete rows; they are *derived* at render time by splitting on paragraph/newline boundaries and auto-numbered, with `#` headings resetting numbering into sections. This keeps the schema a single column and makes URL recipe import (FUTURE.md) a clean target, while leaving every paragraph individually addressable — so a future guided **cook mode** can highlight the current step without a migration. Per-step metadata (timers, durations) would be the trigger to promote directions to `recipe_step` child rows; deferred until that feature exists.
