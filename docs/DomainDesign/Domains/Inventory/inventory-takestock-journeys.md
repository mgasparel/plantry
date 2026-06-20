# Take Stock — User Journey Map

> **Status:** Design in progress — Phase 2 (bd `plantry-5vxb`)
>
> **Purpose:** Checkpoint of the journey-mapping session for **Take Stock** — a low-friction flow to walk your storage Locations and reconcile recorded stock against what is physically on hand. Feeds the Inventory ubiquitous language, domain model, and data-schema passes (next steps).
>
> **Bounded context:** Inventory (`inventory` schema). Take Stock is **not a new primitive** — it is a batch UI that produces `Correction` journal entries (`source_type = Manual`) against existing `ProductStock` aggregates. It reads Catalog for Locations, products, and units.

---

## DDD Process

```
User Journeys (← here)  →  Ubiquitous Language  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Naming (decided with user)

- The feature, page, and primary CTA are **"Take Stock"** — a verb phrase, on-brand, and it avoids conflating with **Inventory** (the *stored data*). You *take stock* to *update your inventory*.
- The action is **count**; surfaces use "counted" (e.g. a product detail can show "Last counted 3 days ago"). There is no user-facing noun for a completed pass — and, per C7, no persisted session object behind one either.

---

## Confirmed Decisions

| # | Decision | Outcome |
|---|----------|---------|
| C1 | Scope | **Audit walk**, location-by-location, with **free partial scope** — the user may count and save a single item, a whole Location, or the entire inventory in one sitting. The same flow serves a quick one-item fix and a full audit alike; no separate mode is needed (see *Partial scope is first-class*). |
| C2 | Input model | **Scalar by default** — one "how many do I have" number per product per location — **with a per-lot escape hatch**. Single-lot products stay a one-number entry; multi-lot / multi-expiry products can be expanded to adjust individual lots. |
| C3 | What persists | The reconciliation result is a batch of **`Correction`** journal entries (`source_type = Manual`). No new write primitive is invented; Take Stock orchestrates existing Inventory operations. |
| C4 | Untracked products | **Excluded** from the walk. `track_stock = false` staples have no lots and are always-satisfied; counting them is meaningless (Inventory invariant R7; untracked-staple definition lives in Catalog / recipes C12). |
| C5 | Countable rows | A Location's walk lists the **non-parent products** (R1 — lots only attach to non-parents) that **belong to that Location**: the **union** of (a) products with live lots here, and (b) tracked products whose **default location** is here but currently have **no stock here** — never inventoried *or* fully depleted — shown with a recorded count of **0** so the user can give them an opening balance (C8). Parent products are display groupings only, never a count row. Tracked products with **neither** lots **nor** a default location surface in a dedicated **"No location" section** (J7) — *not* hidden behind search — where the user assigns a location as they count them. For a mature, large pantry an optional **toggle hides zero-stock rows** to cut noise; default is to show them. |
| C6 | Count semantics | A count is **authoritative** — "there are N, period" (recount semantics): on **Save**, the delta is computed against current stock and applied as set-to-N. |
| C7 | **No resumable session, no draft state** | Counts live only in the page's working set until the user **Saves**. Save commits all pending counts as durable `Correction`s (real stock, immediately). **Leaving without saving discards the pending counts** — there is no draft to resume. A large audit is done by saving in chunks (e.g. per Location); each save is committed truth, not a resumable draft. This deliberately **removes the long-session staleness problem** the earlier C6/J6 worried about. |
| C8 | Upward correction (found more) | Counting **>** recorded creates a new lot with `reason = Correction` (an **opening balance**, *not* a `Purchase` — keeps spend/Pricing data clean). Expiry optional (user can set it via the escape hatch; else null = "no expiry," consumed last per FEFO). **The same path establishes initial stock for an item newly added during a walk** (see D5) — a new item is just the extreme case where prior recorded = 0. |
| C9 | Downward reason | Counting **<** recorded defaults to `reason = Correction` — the honest "unknown adjustment" bucket; we do **not** guess. A **reason selector** (tappable buttons / segmented control, **defaulting to Correction**) lets the user state intent when they know: **"Used it"** → `Consumed` (real consumption — keeps consumption analytics truthful), **"Spoiled / threw out"** → `Discarded` (waste analytics). Anything else (original-entry error, gave away, lost) stays `Correction`. Under scalar entry the delta lands FEFO (earliest-expiry lot first); the escape hatch lets the user place it per-lot. |
| C10 | Counting unit | The count input lets the user enter in **any unit the product has a conversion for** (default: the product's default/display unit). The entered value converts to base for delta math via the existing `IProductConversionProvider`. When a product's lots span multiple units, the recorded total is shown in the default unit. |
| C11 | Multi-location | A count is per **(product, location)** — a product occupying several Locations is counted independently under each; never one global number. **Known limitation:** Take Stock cannot represent a *relocation* — moving stock between Locations reads as a loss in one place + an appearance in another (losing lot expiry/history). **Transfer** remains the correct per-lot tool for moves; this is acceptable and documented, not solved here. |
| C12 | **Inline add / onboarding (v1)** | Take Stock doubles as a new-user **onboarding** path: walk the kitchen and *populate* the pantry, not just reconcile it. An **"+ Add item"** action opens a **search-first** flyout (J5): find an existing Catalog product → add with a count; on no match, **create a product inline**. **Two halves of "the Recipes pattern," both reused exactly:** (1) *UI* — the **same product-search + inline-create sheet** the Recipes editor uses (the shared `searchableSelect` picker plus its "create new" mode). That sheet is currently **bespoke inside `Pages/Recipes/Edit.cshtml`**, so this work must **extract it into a shared component** (registered in the Dev component library per the repo's "extract before you repeat" rule) and have both pages consume it — *not* clone it. (2) *Write path* — an Inventory-owned anti-corruption write port (the analogue of Recipes' `ICatalogWriter`), implemented in `Plantry.Web` over Catalog's `CreateProductCommand`. **Necessary differences from the Recipes staple flow:** the product is created **`trackStock: true`** (it is being stocked, not minted as an untracked staple) with `defaultLocationId` = the Location being walked, and the sheet is wrapped with count/location/reason fields instead of qty/unit/group-heading. Minimal create fields only (name, default unit, count; category optional) — variants/parents/SKUs/photos redirect to the full product editor (no authoring-depth creep). Initial stock is the **C8 opening-balance `Correction`**. Sliceable: "add existing product to a location" is nearly free (C8); "extract the shared sheet + create new product inline" is the larger slice — both ship in v1. *(This is **not** Intake — Intake is the AI/receipt-staging context.)* |

---

## Open Decisions (Deep Dives Needed)

| # | Decision | Context / tradeoff |
|---|----------|--------------------|
| ~~D1~~ | ~~Upward-correction path~~ | **Resolved → C8.** Upward count creates a new `Correction` lot (opening balance, not `Purchase`). The *command*-layer gap (`ConsumeStockCommand` rejects positives, `AddStock` hard-codes intake) is a domain-model task: add an upward-correction operation. |
| ~~D2~~ | ~~Downward reason: Correction vs Discarded~~ | **Resolved → C9.** Default `Correction`; escape-hatch intents "Used it" → `Consumed` and "Spoiled" → `Discarded`. The taxonomy collapses to: real consumption = `Consumed`, waste = `Discarded`, everything-else/unknown (miscount, original-entry error, gave away, lost) = `Correction`. We never auto-guess `Consumed`/`Discarded` from a bare scalar. |
| ~~D3~~ | ~~Counting unit~~ | **Resolved → C10.** Count in any unit the product supports a conversion for; default to the product's default unit; convert to base for delta math. |
| ~~D4~~ | ~~Is a count session a persisted record?~~ | **Resolved → C7.** No persisted session and no resume. Pending counts are page-only until Save; only the `Correction` journal rows persist. A per-product "Last counted N days ago" surface, if wanted, derives from the newest `source_type = Manual` `Correction` row — no new schema. |
| ~~D5~~ | ~~Add items / products during a walk → onboarding tool~~ | **Resolved → C12.** Take Stock doubles as a new-user onboarding path; inline product add is **v1** (scoped by slicing). |
| ~~D6~~ | ~~Multi-location products~~ | **Resolved → C11.** Per (product, location); relocation is out of scope (use Transfer). Location ordering is a display concern (default by name/configured order) — no domain impact. |

---

## Journeys

### J1 — Start counting

**Trigger:** User taps **"Take Stock"** (nav entry / Pantry page).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Lists the household's **Locations** (Catalog), plus a **"No location"** group at the end *when* any tracked product has neither lots nor a default location (J7). No resume prompt — every start is fresh (C7). |
| 2 | User | Picks a Location to count (or accepts the default order), or opens the "No location" group (J7). |
| 3 | System | Opens the location walk (J2), or the no-location flow (J7). |

**Domain events emitted:** none (pure navigation).

**Edge cases:**
- No Locations defined → empty state prompting the user to create a Location first.
- No tracked stock anywhere (new household) → the walk is **additive onboarding** (J5/J6), not reconciliation: prompt the user to start adding what's on their shelves.

---

### J2 — Walk a Location (scalar counts)

**Trigger:** Entering a Location from J1.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Lists the **non-parent products that belong to this Location** (C5): those with lots here, **plus** tracked products defaulted here that have no stock yet (never inventoried / fully depleted), shown with a recorded count of 0. Each row shows its **recorded count** (sum of lot quantities here) in the product's default unit (any supported unit selectable, C10), with an input pre-filled to that recorded count. |
| 2 | User | For each item, confirms (leaves as-is) or types the **actual count**. Unchanged rows record nothing. |
| 3 | System | A changed value is held in the page's working set (pending, not yet saved). Visual cue distinguishes changed / untouched. |
| 4 | User | May mark an item **"0 / none left"** in one tap (common case). |
| 5 | User | Multi-lot product → taps to **expand the lot escape hatch** (J3). |
| 6 | User | Item not in the list → taps **"+ Add item"** to search-or-create it inline (J5). |
| 7 | User | **Saves** the counted items (J4) to commit them, then moves to the next Location — or keeps counting and saves later. Per C7, anything not saved is lost on leaving. |

**Domain events emitted:** none at this step — the working set is page-only until Save (C7).

**Edge cases:**
- Product defaulted here but **never inventoried** (or fully depleted) → shown with recorded 0; entering a count creates the first lot as an opening-balance `Correction` (C8).
- Product has lots in multiple Locations (C11) → counted independently per Location; no global number.
- Tracked product with **no default location and no lots** → surfaces in the **"No location" section** (J7), where the user assigns a location while counting it.
- Untracked staple → never listed (C4).
- Large pantry with many zero-stock defaulted products → optional toggle hides not-in-stock rows (C5).

---

### J3 — Reconcile a multi-lot product (escape hatch)

**Trigger:** User expands a product with more than one lot, or wants expiry/reason control on a single-lot item.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Shows each lot: quantity, expiry, location, open/frozen state. |
| 2 | User | Adjusts individual lot quantities; the product's total updates to match. |
| 3 | User | On a downward change, may set the reason per lot — e.g. **"spoiled / threw out"** → `Discarded` rather than `Correction` (C9). |
| 4 | User | On an upward change, may set an **expiry** for the found stock (else it defaults per C8). |
| 5 | System | Collapsing the escape hatch returns the reconciled total to the scalar row. |

**Domain events emitted:** none (staging).

**Edge cases:**
- Scalar total typed at J2 that the user never expands → system applies the default lot-placement rule (FEFO down; single correction lot up) — C8/C9.
- All lots set to 0 → product drops to zero recorded stock; lots deplete normally (`depleted_at`, retained per R4).

---

### J4 — Save counts

**Trigger:** User taps **"Save"** (commits the current working set — one item, this Location, or everything counted so far).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | For each changed product, under the `ProductStock` row lock (DM-13), computes the delta from the counted value vs **current** stock and writes journal entries: downward → `Consume(reason = Correction\|Consumed\|Discarded)`; upward → upward `Correction` (C8). All `source_type = Manual`, attributed to the user. |
| 2 | System | Confirms ("N items updated") and clears the saved items from the working set. |

**Domain events emitted:** per-product `Correction` journal rows are the durable truth. (No session/aggregate event — C7.)

**Edge cases:**
- A counted value that equals **current** stock (someone already corrected it) → no-op for that item, no journal row.
- Save interrupted (network) → pending counts remain in the page; the user retries. Per-product application is idempotent on re-drive (mirrors the cook adapter's `sourceLineRef` idempotency in `ConsumeStockCommand`).
- `xmin` optimistic-concurrency conflict on a root → re-read current stock and apply the user's count as set-to-N (recount wins; the conflict window is tiny because there is no long-lived session — C7).

---

### J5 — Add an item during a walk (inline create / onboarding)

**Trigger:** User taps **"+ Add item"** while counting a Location (J2). The primary onboarding path for a new user with an empty pantry (C12).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Opens the **same product-search + inline-create sheet the Recipes editor uses** (C12) — the shared `searchableSelect` picker plus its "create new" mode, extracted from `Pages/Recipes/Edit.cshtml` into a shared component and consumed here verbatim. User types a product name; system searches the household Catalog (dedupe is the point — onboarding users would otherwise mint duplicates). |
| 2 | User | **Match found** → selects it; it joins this Location's list as a count row (J2). If it had zero recorded stock here, the count is simply an upward correction from 0. |
| 3 | User | **No match** → taps **"Create '<name>'"** — the sheet's create mode (identical to the recipe staple create), wrapped here with count/location fields. Minimal: name (prefilled), default unit, count; category optional. |
| 4 | System | Creates the Catalog product via the Inventory write port → Catalog `CreateProductCommand` with **`trackStock: true`** and `defaultLocationId` = the current Location (C12). |
| 5 | System | Adds the new product to the Location's list with the entered count, staged like any other row. On **Save** (J4) its initial stock is written as a **C8 opening-balance `Correction`**. |

**Domain events emitted:** Catalog `ProductCreated` (Catalog context, on create); the stock itself follows J4.

**Edge cases:**
- Duplicate name → Catalog rejects on create; the search-first step makes this rare. Surface Catalog's error inline; nudge the user to pick the existing match. *(Stronger fuzzy-match dedupe is tracked separately as bd `plantry-hl4a`, out of scope here.)*
- User wants variants / SKUs / photo / a unit conversion → out of scope for the flyout; link to the full product editor (no authoring-depth creep, C12).
- Brand-new user, empty pantry → the whole walk is additive; there is nothing to reconcile, only to add (see J6).

---

### J6 — Onboarding entry from Today

**Trigger:** A new (or near-empty) household lands on the **Today** home page.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Today detects little/no tracked stock and surfaces a **"Stock your pantry — Take Stock"** prompt as a primary cold-start call to action (guides the user toward population, per user direction). |
| 2 | User | Taps through into Take Stock → J1, landing in the additive walk (J5) rather than a reconciliation of empty shelves. |
| 3 | System | As the user adds items and Saves, the pantry fills; the Today prompt recedes once tracked stock exists. |

**Domain events emitted:** none (Today is a read/compose surface; writes happen via J4/J5).

**Edge cases:**
- This is a **Home (Today)** context change, not Inventory — it composes the existing cold-start machinery on Today and links out to Take Stock. Tracked as a cross-context slice.
- Established household with stock → the prompt does not show; Take Stock is reached from its normal nav entry (J1).

---

### J7 — Count items with no location ("No location")

**Trigger:** From J1, the user opens the **"No location"** group. It appears only when tracked products exist with neither lots nor a default location (typically an **import artifact** — e.g. a Grocy import that left products unplaced).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Lists those tracked non-parent products, each at recorded **0**, with a **required location picker** alongside the count input. |
| 2 | User | For an item: picks a **location** and enters a **count** (escape hatch for expiry/reason as J3, if wanted). |
| 3 | System | On **Save** (J4): writes the opening-balance lot (C8) in the chosen location, **and sets that location as the product's default location** (Catalog write via the C12 port) so the item is placed correctly in future walks and does not return here after depletion. |
| 4 | System | The item leaves the "No location" group; the group disappears once empty. |

**Domain events emitted:** as J4 (per-product `Correction`) + a Catalog product update (default location set).

**Edge cases:**
- User enters a count but **picks no location** → that row cannot save (a lot requires a `location_id`); prompt to choose one.
- User picks a location but **leaves the count at 0** → no lot is created, but the **default location is still set** (a pure Catalog "file this product" cleanup) — the item leaves the group without fabricating stock.
- No unassigned products exist → the "No location" group is not shown in J1.

---

## Cross-Cutting Notes

### Partial scope is first-class

The user wanted the design not to paint us into a corner. Free partial scope (C1, C7) does exactly that on its own:

- **Counting is "save a set of per-(product, location) corrections," at any scope — not a mandatory full sweep** (C1, C7). Saving a single item and saving the whole inventory are the *same* operation at different sizes — so the one flow already covers both a quick fix and a full audit; no second mode is needed, and none is precluded.
- **No "you must visit every location to finish" invariant.** This keeps the flow flexible. (If we ever want a *certified* full audit, that becomes an additional, opt-in completeness check layered on top — not a constraint baked into the base flow.)
- **Reuse of the existing `Correction` taxonomy** means Take Stock and the per-product Pantry edit converge on the same journal semantics — analytics and history stay coherent across whichever entry point the user used.

### Why this is "not a new primitive"

Everything Take Stock commits is expressible as existing Inventory operations (`Consume` with `reason = Correction`/`Consumed`/`Discarded`; plus the upward `Correction` path in C8). The net-new surface is **the batch location-walk UI** (plus C12's inline add) — not new stock mechanics, and (per C7) no new persisted aggregate. This keeps the blast radius small and the Inventory aggregate untouched in shape. The one genuine code gap is the command-layer upward `Correction` (C8) — flagged for the domain-model pass.

### Tradeoffs deliberately being made

1. **Scalar-first loses some per-lot intent** unless the user opens the escape hatch (C2). We accept silent default lot-placement (FEFO down, single correction lot up) for friction's sake — C8/C9 define those defaults so the behaviour is predictable, not arbitrary.
2. **Recount = last-write-wins** (C6). A saved count overrides whatever stock currently shows. This is correct for a recount, and the conflict window is tiny because counts are saved promptly rather than held in a long session (C7).
3. **No resume / save-or-lose** (C7). The cost: a user who counts a lot without saving and then leaves loses that work — mitigated by saving in chunks (per Location) and by a "leave page?" guard on unsaved counts. The benefit: no draft state, no session aggregate, and the long-session staleness problem disappears entirely.
4. **Counting in any supported unit** (C10). Friendlier than stock-unit-only, but it leans on conversions existing: a product with no conversion for the user's natural unit falls back to its default/stored unit. Authoring those conversions is a Catalog concern, not solved here.
