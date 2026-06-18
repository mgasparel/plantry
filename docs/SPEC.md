# Plantry — Product Spec

> Page-first spec. Each page → user journeys → features. Architectural decisions (stack, AI orchestration, auth/tenancy, persistence) live in ARCHITECTURE.md and the product rationale in VISION.md; deferred nice-to-haves are in FUTURE.md. This document describes what users see and do.

---

## Navigation

The primary surface is **Home (Today)** — cooking is the daily heartbeat, so the app lands there first. Navigation is organised by **how often you reach for it**, not by domain.

**Desktop** — a banded sidebar:

| Band | Destinations |
|------|--------------|
| Cook & Plan | **Today** (home), Recipes, Meal Plan¹ |
| Shop | Shopping, Scan receipt |
| Manage | Pantry, Catalog |

Footer: Settings, Sign out.

**Mobile** — a five-item bottom nav: **Today** · Recipes · **Add** · Shop · More.

The **Add** item is a quick-action shortcut (opens the intake sheet from any tab), not a full page. **More** is the overflow grid (Pantry, Catalog, Meal Plan, Deals, Settings).

¹ Meal Plan arrives in Phase 3; Deals in Phase 4.

---

## Page 0 — Home (Today)

**Purpose.** The daily answer to "what needs me today?" — the default landing surface introduced by the nav redesign. Surfaces what's time-sensitive (meals to cook, stock about to expire, work waiting to be reviewed) on one screen, and **degrades gracefully** as later-phase data (Meal Plan, Deals) comes online.

### User journeys

#### 0a. Morning glance
1. User opens the app; Home is the landing page.
2. A greeting header shows the household name and today's date.
3. Below it: any **review banners** (work waiting on the user), a **meals** section (what to cook), and an **expiring-soon** widget (stock about to turn).
4. Each surface is an entry point — tap a meal to cook it, an expiring item to review it in Pantry, a banner to clear the pending work.

#### 0b. Review pending work
1. When intake items have been parsed and are awaiting review (e.g. a forwarded receipt, §2b), a banner reads "N items ready to review."
2. User taps **Review** → the intake review form (§2e); or dismisses the banner for this session.
3. Later phases add more banner kinds to the same stack — the **deal review queue** (§6b, Phase 4).

#### 0c. Decide what to cook
1. The meals section answers "what's for dinner?"
2. **Phase 2:** it shows **cook-now recipe picks** — recipes the household can make from current stock (sorted by fulfillment; recipes using expiring ingredients favoured), each a **Cook** entry point (§4c).
3. **Phase 3+:** it shows the day's **planned meals** from the weekly Meal Plan (§5) — typically one per meal slot, but a slot may hold more than one (e.g. a separate meal for a member on a different diet) — each a Cook entry point with a live "ready / N to pick up first" fulfillment hint and a link to the weekly grid. Empty slots invite planning.

#### 0d. Head off waste
1. The **expiring-soon** widget lists stock within the expiry window (default 7 days, §1d), soonest first, distinguishing **expired** from **expiring**.
2. Each row shows the product, quantity·location, and a "Today / Tomorrow / N days" pill.
3. User taps through to review in Pantry, or uses **"Use it up"** to find recipes that consume the expiring items (→ Phase 3 "Use it up").

#### 0e. Cold start (new household)
1. A household with no stock, recipes, or pending intake sees a **welcome hero** instead of empty widgets.
2. Three guided steps: **Add groceries** (intake) → **Browse recipes** → **Plan your week**.
3. Widgets adopt onboarding empty states ("Add groceries and Plantry watches the clock") rather than the "all clear" good state.

### Features
- Default post-login landing surface; both navs lead here first.
- Greeting header: household name + current date.
- **Review banner stack** — dismissible per session, one row per pending-work kind; intake (Phase 2), deals (Phase 4). Renders nothing when no work is pending.
- **Meals section** — cook-now recipe picks (Phase 2) → today's planned meals (Phase 3+); each a Cook entry point.
- **Expiring-soon widget** — top-N items in the expiry window, soonest-first, expired-vs-soon distinction, "Use it up" CTA.
- Three states: **cold** (welcome hero + onboarding empties), **active** (pending work / expiring stock), **all-clear** (good-state empties — "nothing expiring this week").
- Graceful degradation: every widget renders meaningfully on Phase-2 data; Meal Plan and Deals enrich in place when their phases land.

### Phasing
| Surface | Data source | Phase |
|---------|-------------|-------|
| Greeting + shell + cold-start welcome | Identity (household) | 2 |
| Expiring-soon widget | Inventory (`ExpiryTone` within window) | 2 |
| Review banner — intake | Intake sessions in `Ready` status | 2 |
| Meals — cook-now picks | Recipes browse (fulfillment) | 2 |
| Meals — today's planned meals | Meal Planning (P3-3 / P3-4) | 3 |
| Review banner — deals | Deals review queue | 4 |

---

## Page 1 — Pantry

**Purpose.** The source of truth for what you currently have at home.

### User journeys

#### 1a. Browse inventory
1. User opens Pantry tab.
2. Products are listed, grouped by category (or location). Each row shows: product name, total quantity on hand (aggregated across all stock entries and SKUs), and an expiry indicator if anything is expiring soon.
3. User can search or filter by category/location.

#### 1b. View product detail
1. User taps a product row.
2. Detail sheet opens showing:
   - All individual stock entries (quantity, unit, expiry date, location, date purchased).
   - Consumption history (recent events).
   - Link to edit the product (catalog view).
3. User can manually consume stock from here ("I used some of this").
4. User can transfer a stock entry to another location (e.g., move to freezer) — expiry recalculates on transfer (see §Cross-cutting: Freeze/Thaw Expiry).

#### 1c. Manual consumption
1. From product detail, user taps "Consume".
2. Enters quantity and unit.
3. System applies FEFO: deducts from the soonest-expiring stock entry first.
4. Entry is written to the stock journal.

#### 1d. Expiry review
1. Expiring-soon items (configurable threshold, default 7 days) surface with a visual flag in the list.
2. Optionally a dedicated "Expiring soon" filter shows only those items.
3. From here (or product detail) the user can **throw out** an item that has gone bad — logged as waste, not normal use (see §Cross-cutting: Consumption & waste).

### Features
- Stock aggregated at the product level (all SKUs summed).
- FEFO ordering — oldest-expiry stock consumed first.
- Quantity + unit displayed in the product's preferred display unit (e.g., grams → "500 g", not "0.5 kg").
- Search and filter by category and/or location.
- Group-by toggle: category vs. location.
- Expiry badge with configurable warning threshold.
- Stock transfer between locations with automatic expiry recalculation on freeze/thaw.

---

## Page 2 — Add Stock (Intake)

**Purpose.** Log what you bought as fast as possible. Two entry points, one review form.

### User journeys

#### 2a. Receipt photo upload (synchronous)
1. User taps Add Stock tab → "Scan Receipt".
2. Takes a photo or picks from gallery.
3. App sends image to AI (Claude vision).
4. Loading state while AI parses (~5–15 s).
5. Import review form appears (see §2e).

#### 2b. Receipt via email (async)
1. User forwards receipt email to a dedicated app address (configured in settings).
2. App processes the email in the background.
3. Next time user opens the app, a banner appears: "3 items ready to review."
4. Tapping the banner opens the same import review form.

#### 2c. Manual product add
1. User taps Add Stock tab → "Add manually".
2. Product search field — type to search catalog.
3. Select product (or create new one inline).
4. Enter quantity, unit, location, expiry date (optional — auto-suggested from product defaults + location type).
5. Confirm — entry written to journal.

#### 2d. New product creation (inline during intake)
1. During manual add or on the import form, an unmatched item has a "Create product" action.
2. Inline form: name, category, default unit.
3. Optionally add a SKU (size/variant) at this point.
4. New product saved to catalog; intake continues.

#### 2e. Import review form (shared by 2a and 2b)
This is the central UI for AI-assisted intake. Every receipt parse ends here.

**Form structure:**
- Each line item from the receipt is a row.
- Matched rows show: receipt text | → | matched product name | quantity | unit | location | expiry | price paid.
  - The expiry field is a **Date / Never** toggle. "Never" posts null expiry (the item does not expire); "Date" reveals a date picker pre-filled from the product's `default_due_days` default.
  - When a product is selected or changed in the drawer, location, unit, and expiry auto-recompute from that product's catalog defaults.
- Matched rows with multiple plausible catalog candidates show a **"Did you mean" chip strip** (AI-ranked alternatives). The top-ranked match is pre-selected; tapping a chip switches the product selection and triggers the same default-prefill recompute.
- Unmatched rows show: receipt text | "Unrecognized item" label | open drawer with "Create product" or "Link to existing" controls.
- User can edit any field before submitting.
- "Submit all" writes all confirmed rows to the stock journal as purchase events.
- Individual rows can be dismissed ("Not pantry stock — remove"); dismissed rows can be restored.

**Match confidence:**
- High confidence matches are auto-filled and shown normally.
- Low confidence matches are highlighted prompting a quick review.
- Unmatched items are shown with a distinct treatment and drawer open by default.

### Features
- Two intake modes (sync upload, async email) converging on the same review form.
- AI vision receipt parsing (Claude).
- Per-row edit before commit.
- AI-ranked "Did you mean" chip alternatives when multiple catalog products match the same receipt line.
- Date / Never expiry toggle per row (null expiry = item does not expire).
- Product-default prefill: selecting a product in the drawer auto-fills location, unit, and expiry from the product's catalog defaults; re-selects whenever the product changes (AI chip, dropdown).
- Inline product creation from unmatched rows.
- Price captured per SKU for price history.
- Stock journal entries written with: product, SKU (if known), quantity, unit, location, expiry, purchase price, timestamp.

---

## Page 3 — Shopping List

**Purpose.** Build and manage what you need to buy.

### User journeys

#### 3a. View shopping list
1. User opens Shopping tab.
2. Items grouped by category (mirrors store layout, if configured).
3. Each item shows: product name, quantity needed, unit, optional note.
4. Optionally shows deal indicator ("On sale at FreshCo this week").

#### 3b. Add item manually
1. Tap "+" or type in quick-add bar.
2. Search for product or type free text.
3. Optionally set quantity and note.
4. Item added to list.

#### 3c. Check off items while shopping
1. User taps item to mark as picked up.
2. Checked items move to bottom (or can be hidden).
3. After shopping, user goes to Add Stock (§2c or §2a) to log the actual purchases.

#### 3d. Add missing recipe ingredients
1. From a recipe detail page, tap "Add missing to shopping list."
2. All ingredients not currently in stock (or below threshold) are added.

#### 3e. Clear completed
1. After a shopping trip, user taps "Clear checked" to remove picked-up items.

#### 3f. Deal overlay (Phase 4)
1. Items on the list that have an active deal at a local store show a deal badge.
2. Tapping shows the deal detail: store, price, valid until.

### Features
- Manual item add with product search.
- Category grouping.
- Check-off with persistence until cleared.
- "Add missing ingredients" integration from recipe pages.
- Deal badge overlay when a deal exists for listed products.
- Notes per item.

---

## Page 4 — Recipes

**Purpose.** Browse, manage, and cook from your recipe collection.

### User journeys

#### 4a. Browse recipes
1. User opens Recipes tab.
2. Grid or list of recipes with photo, name, fulfillment %, and cost-per-serving.
3. Filter by tag (vegetarian, spicy, etc.) or search by name.
4. Sort options: fulfillment %, cost, name, recently added.

#### 4b. View recipe detail
1. User taps a recipe card.
2. Recipe detail page shows:
   - Photo.
   - Tags, servings, cook time (if set).
   - Fulfillment badge: "You have 7 of 9 ingredients (78%)". For ingredients that reference a parent product, stock is summed across all child variants.
   - Cost per serving (from price history and/or live deals).
   - Ingredient list with per-ingredient stock status (have / missing / low).
   - Directions (rendered Markdown).
3. Action buttons: Cook, Edit, Add missing to shopping list.

#### 4c. Cook a recipe
1. User taps "Cook" on recipe detail.
2. Servings selector (default: recipe's serving count).
3. Ingredient review screen:
   - Each ingredient shown with stock available vs. required.
   - For any ingredient that references a **parent product**, a **disambiguation picker** is shown before confirming. The picker lists all child variants whose unit matches the recipe ingredient's unit; variants with a non-matching unit are excluded and cannot be selected. The user can split the required quantity across multiple matching variants (e.g. 100 g Roma + 300 g Beefsteak for a 400 g Tomato ingredient). If no variant matches the unit, the ingredient is treated as a stockout — same behaviour as any other missing ingredient, no special handling.
   - For any ingredient, user can:
     - **Swap**: replace with a different product (manual substitution).
     - **Skip**: omit the ingredient.
     - Leave as-is.
4. Confirm → stock is consumed (FEFO, journal entries written), servings logged.

> _AI recipe import from URL has moved to FUTURE.md (nice-to-have, not in-scope for the core build)._

#### 4d. Create a recipe manually
1. Tap "New Recipe".
2. Fill: name, servings, photo upload, tags.
3. Add ingredients: product search + quantity + unit + optional heading.
4. Enter directions in Markdown editor.
5. Save.

#### 4e. Edit a recipe
1. From recipe detail, tap "Edit".
2. Same form as create; all fields editable.
3. Ingredients can be reordered, deleted, or have headings added.

### Features
- Fulfillment scoring (% ingredients on hand, product-level, FEFO-aware; stock rolled up across all variants when ingredient references a parent product).
- Cost per serving — two tiers: from purchase history, and deal-aware (cheapest this week).
- Per-ingredient stock status in the ingredient list.
- Parent-product disambiguation picker at cook time: user selects which variant(s) to consume and may split quantities across variants; only variants whose unit matches the recipe unit are shown.
- Manual substitution (swap/skip) at cook time — no AI substitution.
- Ingredient grouping via headings.
- Tag filtering.
- Directions stored as Markdown; rendered and editable in-app.
- Photo upload.

---

## Page 5 — Meal Plan

**Purpose.** Plan meals for the week, driven by inventory, expiry, and deals.

### User journeys

#### 5a. View the current week's meal plan
1. User opens Meal Plan (via More tab).
2. Weekly calendar view — each day shows a row per **configured meal slot** (see §7h). Slots are user-defined free-text labels per household ("Breakfast", "Lunch", "Afternoon snack", "Dinner") in a fixed order; a household that only plans dinners configures a single "Dinner" slot.
3. Each cell holds an **ordered stack of meals** — usually one, but a slot can carry several (e.g. one meal for Mike and another for Jane), each its own card. Each meal card shows: recipe name(s), fulfillment %, estimated cost.

#### 5b. Generate a meal plan with AI
1. Tap "Generate plan."
2. User optionally sets constraints:
   - Dietary tags to prefer/exclude.
   - Which meal slots to fill and over how many days (e.g., "Dinner for 5 days", or "Lunch + Dinner all week").
   - Budget target.
   - Prefer expiring ingredients (on by default).
3. The planner runs server-side, reading from Plantry's own application services:
   - Pulls current pantry state.
   - Checks expiring stock.
   - Evaluates recipe fulfillment.
   - Checks active deals.
   - Composes a week of meals prioritizing: high fulfillment, expiring stock, low cost.
4. Proposed plan shown for user review — each slot with recipe + reasoning snippet ("Uses chicken that expires Friday").
5. User can accept all, swap individual meals, or regenerate.
6. Confirmed plan is written to the meal plan (the proposal only persists on confirmation).

#### 5c. Manually assign a meal
1. Tap any empty day/slot — or **"Add meal"** on a filled slot to stack another meal alongside the existing one(s).
2. Recipe picker — searchable, filterable.
3. Select recipe → assigned to that slot. Editing an existing meal updates only that meal; adding leaves the others intact.

#### 5d. Add missing ingredients to shopping list
1. From the meal plan view, tap "Shop for this week."
2. All ingredients needed for the planned meals that are not in stock are added to the shopping list.

### Features
- Weekly calendar view with user-configurable, free-text meal slots per household (§7h).
- AI-generated plan (server-side planner using stock, expiry, recipe fulfillment, deals; see ARCHITECTURE.md ADR-007).
- User constraints input (tags, which slots × how many days, budget, prefer-expiring).
- Reasoning snippets on AI-generated slots.
- Swap/regenerate individual slots.
- "Shop for this week" → bulk add missing ingredients to shopping list.
- Manual slot assignment.

---

## Page 6 — Deals

**Purpose.** Browse this week's flyer deals, see what's matched to your products, and act on deals.

### User journeys

#### 6a. Browse active deals
1. User opens Deals (via More tab).
2. Deals list filtered to currently-active deals from configured local stores.
3. Deals matched to catalog products show the product name and a link to the product.
4. Deals in pending review are visually distinguished.

#### 6b. Deal review queue
1. A "Review" filter or section shows pending deals (unmatched or low-confidence matches).
2. Each pending deal shows: raw deal name, brand, price, sale story, and AI-proposed match.
3. User can:
   - **Confirm** the AI-proposed match.
   - **Correct** — search catalog and select the right product.
   - **Reject** — mark as irrelevant.
4. Confirmed and corrected matches are stored; the (merchant, normalized_name) key is remembered so the same item doesn't need review again.

#### 6c. Stock-up alert
1. Products the user buys frequently that have an active deal are surfaced as "Stock-up alerts."
2. Shown as a banner or badge on the Deals page and optionally as a push notification.
3. Tapping adds to shopping list.

### Features
- Active deals list with merchant, price, valid dates, and matched product.
- Pending review queue for low-confidence and unmatched deals.
- Confirm / correct / reject actions with match memory.
- Stock-up alerts for high-frequency products with active deals.
- Deal data stored indefinitely as price history (feeds recipe costing).

---

## Page 7 — Settings & Catalog

**Purpose.** Configure the app and manage the product catalog.

### Sub-pages

#### 7a. Product Catalog
- List of all products.
- Search and filter.
- Tap product → product detail: name, category, default unit, default location, SKUs, unit conversions.
- Edit product or add a new one.
- Add/edit SKUs (size variants).
- Add unit conversions (with density auto-suggest when weight↔volume gap detected).

**Product Groups (parent/variant hierarchy)**

A product can optionally be assigned a **parent product**, making it a *variant* of that parent. The parent is an abstract product — it exists for grouping and recipe authoring only; no stock is tracked directly on it. Examples:

- Parent: *Tomato* → Variants: *Roma Tomato*, *Cherry Tomato*, *Beefsteak Tomato*
- Parent: *Canned Beans* → Variants: *Black Beans (398 mL)*, *Chickpeas (398 mL)*

Rules:
- Parents are created and managed in the catalog like any other product but cannot hold stock entries.
- Variants have their own units, independent of the parent.
- Variants are listed under their parent on the parent's product detail page; a variant's detail page shows a link back to its parent.
- Max depth is one level — a variant cannot itself be a parent.

**Per-product expiry defaults (4 values, all optional):**
| Field | Applied when |
|---|---|
| Default due days | Item is stored at a non-frozen location |
| Default due days after opening | Item is marked as opened |
| Default due days after freezing | Item is transferred to a frozen location |
| Default due days after thawing | Item is transferred out of a frozen location |

These fields auto-populate the expiry date during intake and on location transfer. All four are optional; if unset, expiry is left blank and the user fills it in manually.

#### 7b. Locations
- List of configured locations (e.g., Fridge, Freezer, Pantry, Counter, Cellar).
- Each location has: name and type — **frozen** or **ambient**.
  - Frozen locations trigger the freeze/thaw expiry logic (see §Cross-cutting: Freeze/Thaw Expiry).
  - Ambient covers both refrigerated and room-temperature storage; expiry distinction between those is handled by per-product `default_due_days`.
- Add, rename, or delete locations.
- A "default location" can be set per product in the catalog (§7a); used to pre-fill the location field during intake.

#### 7c. Units & Conversions
- List of all units (short + full name).
- Add custom unit.
- List of universal conversions (within-dimension only).
- Product-specific conversions managed from product detail (§7a).

#### 7d. Intake Settings
- Email intake: configure forwarding address.
- Default expiry days by product category (fallback when no per-product default is set).

#### 7e. Stores & Deals
- Configured local stores (for Flipp integration).
- Manage which stores to pull deals from.

#### 7f. App Settings
- AI provider/API key (held server-side; never exposed to the client — see ARCHITECTURE.md ADR-007).
- Expiry warning threshold (days).
- Theme (light/dark/system).

#### 7g. Account & Household
- The **household** is the unit everything is scoped to; all members share the same pantry, catalog, recipes, shopping list, locations, deals, and meal plan (see ARCHITECTURE.md ADR-008).
- Manage your own login (email, password).
- Invite or remove household members.
- v1 is flat — every member has equal rights. Per-member roles/permissions are deferred.

#### 7h. Meal Slots
- An ordered list of **meal slots** the household plans around — free-text labels like "Breakfast", "Lunch", "Afternoon snack", "Dinner".
- Add, rename, reorder, or remove slots.
- Seeded with a sensible default (e.g., Breakfast / Lunch / Dinner); a household that only plans dinners can reduce this to a single "Dinner" slot.
- Drives the Meal Plan grid rows and the "which slots to fill" generation constraint (§5). Backed by the `MealSlotConfig` aggregate in the Meal Planning context (ARCHITECTURE.md ADR-010).

---

## Cross-cutting: Freeze/Thaw Expiry

When a stock entry is transferred between locations, expiry recalculates based on the direction of transfer and the product's 4 expiry-day defaults.

| Transfer direction | New expiry calculation |
|---|---|
| Ambient → Frozen | `freeze_date + default_due_days_after_freezing` |
| Frozen → Ambient | `thaw_date + default_due_days_after_thawing` |
| Ambient → Ambient | No recalculation (expiry unchanged) |

A stock entry records `frozen_at` and `thawed_at` timestamps to support this. If the relevant product default is not set, the user is prompted to enter the expiry date manually after the transfer.

**Intake auto-suggest:** When a user selects a frozen location during intake, the system uses `default_due_days_after_freezing` to pre-fill the expiry field. When a non-frozen location is selected, it uses `default_due_days`.

**Opening:** Separate from freeze/thaw — when a user marks a stock entry as opened (e.g., a bottle of milk), expiry recalculates using `default_due_days_after_opening` from today's date. This is location-independent.

---

## Cross-cutting: Consumption & waste

Every way stock leaves the pantry runs through one consumption operation (ARCHITECTURE.md ADR-011), so consumption history and waste analysis stay consistent no matter the trigger.

- **Manual consume** (§1c) and **mark-a-lot-gone** (§1b) — direct consumption; FEFO by default, or a specific lot when the user is acting on one ("this carton is empty").
- **Cook** (§4c) — consumes each ingredient at the chosen servings. Swaps consume a different product; skips consume nothing. All of it compiles down to the same consume operation per ingredient.
- **Throw out expired** — from expiry review (§1d), the user can discard an item; this is logged as **waste**, distinct from normal use.

Each removal records *why*: **Consumed (used)** vs **Discarded (wasted/expired)** vs **Correction**. This split is what makes future waste-reduction and consumption-trend analysis (VISION Phase 4) possible — it can't be reconstructed later if everything is logged as a generic "consume."

---

## Cross-cutting: AI receipt review form (shared component)

Referenced by both Add Stock (§2e) and could be extended to Deals (§6b). Core interaction pattern:

- Tabular review UI.
- Each row = one parsed item.
- Editable fields inline.
- Confidence colour-coding.
- Unmatched rows with inline create/link actions.
- Bulk submit.

This is the most important UI component in the app — it's what makes the intake fast. It must handle variable-length receipts, inline editing without losing position, and graceful handling of partial data from the AI.

---

## Out of scope (decided, not planning)

- Barcode scanning.
- Natural language intake ("I bought eggs").
- AI substitution engine (substitutions are manual at cook time).
- External product lookup (Open Food Facts, etc.).
- Duplicate detection in catalog.

---

## Open questions remaining

- Fulfillment scoring algorithm detail (how to handle unit mismatches, partial stock).
- ~~Meal plan slot model~~ — **resolved:** user-configurable free-text meal slots per household (§5, §7h).
- Receipt image → text: Claude vision vs. dedicated OCR (ARCHITECTURE.md ADR-007 open item).
- Stock-up alerts delivery: responsive web app ships first; web-push notifications require PWA install and are an enhancement, so v1 alerts may be in-app only.
- Email intake: dedicated mailbox setup, or a specific subject-line trigger?

> Recipe import from URL is no longer an open question here — it moved to FUTURE.md.
