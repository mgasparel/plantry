# Deals — User Journey Map

> **Status:** Complete (approved) — Phase 4. First stage of the Deals design chain; feeds the ubiquitous
> language, domain model, and data schema (next steps). Anchored on [SPEC.md](../../../SPEC.md) §6
> (Deals), §7e (Stores & Deals), §3f (deal overlay on the shopping list) and §0b (deal review
> banner); [VISION.md](../../../VISION.md) ("Deal awareness baked in"); [ADR-010](../../../ADRs/ADR-010.md)
> (Deals aggregates + the ACL classification), [ADR-007](../../../ADRs/ADR-007.md) (untrusted external
> source → review-then-commit), and the Pricing context's already-built deal seam ([DM-16](../../DataModels/index.md)/[DM-17](../../DataModels/pricing.md)).
>
> **Bounded context:** Deals (`deals` schema, Phase 4). A **core** context. References Catalog,
> Pricing, Shopping, Inventory, Identity **by ID only** (DM-3); writes `deal` price observations to
> Pricing on confirm (the seam Pricing left open). The `catalog.store` reference table lands with this
> phase (Catalog-owned, DM-16).

---

## DDD Process

```
User Journeys (← here)  →  Ubiquitous Language  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Confirmed Decisions

| # | Decision | Outcome |
|---|----------|---------|
| D1 | Deal ingestion mechanism | **Flipp, via its unofficial API / scraper.** A background pull fetches each subscribed store's current flyer as **structured** items (merchant, item name, brand, size, price, sale story, validity window). The source is **untrusted** and **fragile** (no official public API), so it is wrapped behind an **anticorruption-layer adapter** (`IFlyerSource`) in `Plantry.Deals.Infrastructure`; the domain never sees a Flipp DTO, only a normalized `RawDeal`. Resolves the long-standing VISION "Flipp access" open question and ADR-010's deferred "Deals ACL specifics." |
| D2 | Where the ACL lands (persisted vs transient) | **Persisted staging, like Intake — *not* transient like Meal Planning.** Flyer pulls are **async, batched, and reviewed over time** (the review queue can sit until the user opens the app, SPEC §0b), so the raw pull and the proposed matches must survive a session. The raw flyer payload is quarantined in a `FlyerImport` provenance aggregate; the reviewable rows are `Deal` records carrying `status`. This is the deliberate mirror of Intake's `ImportSession`/`ImportLine` (ADR-007) and the deliberate **opposite** of Meal Planning's transient pending store (MP-O7). |
| D3 | Two-stage pipeline (mirrors Intake) | **Stage 1 = ingest/normalize** (Flipp pull → `RawDeal` per flyer item, quarantined in `raw_flyer` jsonb). **Stage 2 = match** each `RawDeal` to a `catalog.product` (suggested product + `high`/`low`/`none` confidence + reasoning). The match is the **only AI/heuristic step** — unlike Intake, the *parse* is already structured by Flipp, so the untrusted surface is the **feed itself** (provenance, freshness, ToS) and the **match suggestion**, not free-text OCR. |
| D4 | Match memory (skip re-review) | **`DealMatchMemory` keyed `(store_id, normalized_name) → product_id`** (SPEC §6b step 4). On ingestion, a `RawDeal` whose `(store, normalized_name)` is already remembered is **auto-confirmed** (skips the queue). An unremembered deal is matched by AI/heuristic and **always reviewed at least once** — confirming or correcting it **writes the memory**, so the next flyer's repeat of that item never needs review. Memory is the auto-confirm key; AI confidence only shapes the review-form treatment, it never auto-confirms on its own (the human is the first-time trust boundary, ADR-007). |
| D5 | The human is the trust boundary | **Only user-confirmed (or memory-auto-confirmed) deals cross into Pricing.** A `pending` deal is visible in the review queue and the "pending" section of the Deals page (§6a) but is **not** an active deal and contributes **nothing** to costing, shopping badges, or stock-up alerts until confirmed. Confirm / Correct / Reject are the three review verbs (§6b). |
| D6 | Confirm projects a `PriceObservation` | **Confirming a deal writes exactly one `pricing.price_observation`** (`source=deal`, `valid_from`/`valid_to` = the deal window, `store_id`, `unit_price` materialized, `source_ref = deal_id`) — the seam Pricing already designed and left open ([DM-17](../../DataModels/pricing.md)). Deals owns **no** costing math: "cheapest active deal" is a **Pricing** read model that Recipes and Meal Planning already consume, so deal-aware costing **lights up automatically** the moment Deals starts writing deal observations. |
| D7 | Store identity vs. flyer subscription | Two separate things. **`catalog.store`** is the *merchant identity* — stable per-household reference data of the same shape as `Location`/`Unit`/`Category` (Catalog-owned, [DM-16](../../DataModels/index.md)); its table **lands this phase**. Deals owns a **`StoreSubscription`** config — *which* of those stores the household pulls flyers from (§7e "Manage which stores to pull deals from"). Subscribing to a new merchant ensures a `catalog.store` row, then a subscription referencing it by ID. |
| D8 | Deals **prices**, never **stock** | Deals is a pure price/awareness context. Confirming a deal **never** touches Inventory — a deal is an *advertised price*, not a purchase. Stock only changes when the user actually buys the item and logs it through Intake (§2). The only write Deals makes downstream is the `deal` `PriceObservation` (D6) and, on a stock-up-alert tap, a Shopping list item (D10). |
| D9 | Deal validity & the "active" lifecycle | A deal carries a **validity window** (`valid_from`/`valid_to`) from the flyer. "**Active**" = `valid_from ≤ today ≤ valid_to` **and** `status = confirmed`. The Deals page (§6a) defaults to active deals; expired deals are **retained** (never deleted) as price history — their `price_observation` stays in Pricing indefinitely, feeding cost trends (SPEC §6 "Deal data stored indefinitely as price history"). |
| D10 | Stock-up alerts | **Frequently-bought products that have an active confirmed deal** surface as **stock-up alerts** (§6c) — a read model over (purchase-frequency × active-deal). Surfaced as a banner/badge on the Deals page and, optionally, the Home review-banner stack (Phase-4 banner, [plantry-bpw]). Tapping an alert **adds the product to the shopping list** — reusing the **P2-4 `IShoppingListWriter.AddItems` seam** with `source="deal"`. Push notification is an **enhancement** (needs PWA install), not v1 (VISION open question). |
| D11 | Deal badge on the shopping list (read-time, Shopping-owned) | The deal indicator on a shopping-list item (§3a/§3f) is a **read-time join** computed by **Shopping** against a Deals read model (`IActiveDealReader.ForProducts`), **never stored** on the shopping item ([DM-18](../../DataModels/index.md)). Deals *supplies* the active-deal-per-product read model; it does not own or mutate the shopping list. Same pattern as the recipe cost badge reading Pricing. |
| D12 | Manual deal entry | **Deferred (not v1).** With Flipp as the ingestion mechanism (D1), v1 has no hand-entry form for deals. Manual entry (type in a store/product/price/window) is a clean future extension — it would create a `Deal` with `source = manual` and `flyer_import_id = null`, taking the same confirm path — but it is **out of scope** for the Phase-4 build. Noted so the model leaves room (a nullable `flyer_import_id` + a `source` discriminator), not built. |

---

## Open Decisions — to resolve in the domain-model pass

These are the questions the journey map surfaces but does not settle; the **domain-model pass**
resolves each (as MP-O1…MP-O7 did for Meal Planning).

| # | Decision | Context | Leaning |
|---|----------|---------|---------|
| DL-O1 | `Deal` as its own aggregate root vs. a child of `FlyerImport` | D2/D3 | **Lean: `Deal` is its own root**, `FlyerImport` is a lightweight provenance/ACL envelope it soft-refs. Unlike an `ImportLine` (reviewed once, then inert), a `Deal` has an **independent lifecycle** — browsed while active, feeding Pricing across its whole window, expiring on its own clock — so coupling it to a transient pull envelope (Intake's session+line shape) reads wrong. To confirm in the model. |
| DL-O2 | Is `FlyerImport` an aggregate at all, or just provenance metadata on each `Deal`? | D2/D3 | **Lean: keep it** as a thin root holding the raw pull payload (`raw_flyer` jsonb), the async **pull status** (`pulling`/`parsed`/`failed`), the flyer window, and dedup keys — the analog of `import_receipt`+session status. It earns its place as the async-worker unit and the de-dup anchor (DL-O5). |
| DL-O3 | Auto-confirm via match memory — at ingestion, or surfaced as "auto-confirmed, undoable"? | D4/D5 | **Lean: auto-confirm at ingestion but visible** — memory-matched deals land `confirmed` (write to Pricing immediately) yet appear in the Deals list with an "auto-matched" marker the user can still **Correct/Reject**, which *updates* the memory. Avoids a queue full of obvious repeats while keeping the human authoritative (C12-style). |
| DL-O4 | Where "purchase frequency" for stock-up alerts is read from | D10 | **Lean: Inventory's stock journal** (`Purchase`-reason rows per product) is the truest "what we actually buy"; Pricing's purchase observations are an alternative. A read-only `IPurchaseFrequencyReader` port hides the choice — settle the owning context in the model/app pass. |
| DL-O5 | Idempotent / de-duplicated ingestion | D1/D2 | **Lean: dedup on `(store_id, flyer_external_id)`** (or a content hash, mirroring `import_receipt.sha256`) so re-pulling the same flyer doesn't double-create deals or double-write price observations. A re-pull updates the existing `FlyerImport`/`Deal`s rather than appending. |
| DL-O6 | What "normalized_name" is (the match-memory + dedup key) | D4 | **Lean: a deterministic normalization** of the flyer item name (lowercase, trim, strip pack-size/units/punctuation), computed in the ACL — *not* AI-derived (the memory key must be stable and reproducible across pulls). To specify in the model. |
| DL-O7 | Confidence taxonomy reuse | D3 | **Lean: reuse Intake's `high`/`low`/`none`** (DM-15) verbatim for `Deal.match_confidence` so the review-form treatment, "show low-confidence only" filter, and mental model carry over from the receipt review the user already knows. |

---

## Journeys

### DJ1 — Configure which stores to pull deals from

**Trigger:** User opens Settings → Stores & Deals (§7e).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Loads the household's current **store subscriptions** (which merchants we pull flyers from) and their last-pull status. |
| 2 | User | Searches for a local merchant (Flipp's store directory, surfaced via the `IFlyerSource` adapter) and **subscribes** to it. |
| 3 | System | Ensures a **`catalog.store`** row exists for that merchant (reference identity, DM-16) and records a **`StoreSubscription`** referencing it by ID. The next ingestion cycle (DJ2) will pull this store's flyer. |
| 4 | User | Optionally **unsubscribes** from a store (stops future pulls; existing confirmed deals and their price history are retained, D9). |

**Domain events emitted:** none (configuration).

**Edge cases:**
- The merchant isn't in Flipp's directory → the subscription can't be created; the user is told it's unsupported (the Flipp dependency is explicit here, D1).
- Re-subscribing to a previously-removed store → reuses the existing `catalog.store` row (stable identity) and its `DealMatchMemory` (so previously-learned matches still skip review, D4).

---

### DJ2 — Ingest a flyer (background pull → match → queue)

**Trigger:** The scheduled ingestion worker runs for a subscribed store (async, like the email-intake
processor — no user present). Not a user-facing journey; it produces the work DJ3/DJ4 act on.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | Worker | For each `StoreSubscription`, the **`IFlyerSource`** adapter pulls the store's current flyer from Flipp. De-dups against the last pull (`(store_id, flyer_external_id)` / content hash, DL-O5) — an unchanged flyer is a no-op. |
| 2 | System | Creates (or updates) a **`FlyerImport`** holding the **raw pull payload** in `raw_flyer` jsonb (the ACL quarantine), the flyer's validity window, and `status: parsing`. |
| 3 | System | **Stage 1 — normalize:** each flyer item becomes a **`RawDeal`** (item name, brand, size, price, sale story, window), with a deterministic **`normalized_name`** (DL-O6). |
| 4 | System | **Stage 2 — match:** for each `RawDeal`, first look up **`DealMatchMemory(store_id, normalized_name)`**. If **remembered** → attach that `product_id`, **confidence `high`**, and mark for **auto-confirm** (D4). Else run the AI/heuristic catalog match → `suggested_product_id` + `high`/`low`/`none` confidence + reasoning (quarantined in `raw_flyer`/match fields). |
| 5 | System | Materializes a **`Deal`** per `RawDeal`: memory-matched ones land **`confirmed`** (→ DJ4 writes proceed immediately); the rest land **`pending`** for review. `FlyerImport.status → parsed`. |
| 6 | System | For each newly-`confirmed` deal, runs the **confirm side effects** (DJ4 steps 3–4): writes a `deal` `PriceObservation` and refreshes match memory. |
| 7 | System | Emits a signal that **N deals are pending review** for this household (feeds the Home review-banner, §0b / [plantry-bpw], and the Deals-page review section). |

**Domain events emitted:** `FlyerImported(householdId, flyerImportId, storeId, pendingCount, at)`;
`DealConfirmed(...)` per auto-confirmed deal (DJ4).

**Edge cases:**
- Flipp pull fails / source unreachable → `FlyerImport.status: failed`, `error_detail` set (mirrors Intake `failed`); no partial deals; the worker retries next cycle. The fragile-source risk (D1) surfaces here, contained.
- A pulled flyer is byte-identical to the last (DL-O5) → skipped, no new `FlyerImport`.
- A `RawDeal` matches **no** catalog product even by AI → `Deal` lands `pending` with `confidence: none` ("Unrecognized") — reviewed via Correct/Reject (DJ4), same as an Intake unmatched row.
- The same product is on sale at **two** subscribed stores → two `pending`/`confirmed` deals; both become price observations; "cheapest active deal" (Pricing) naturally picks the lower (D6).

---

### DJ3 — Browse active deals

**Trigger:** User opens Deals (via the More tab / nav, §6a).

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Lists **active** deals — `status = confirmed` and within their validity window (D9) — grouped/sorted sensibly (e.g. by store or category). |
| 2 | System | Each matched deal shows the **catalog product name** and links to that product; an **auto-matched** marker where memory drove it (DL-O3). The store, sale price, and "valid until" date show per deal. |
| 3 | System | **Pending** deals (awaiting review) are **visually distinguished** (§6a) — they are not yet active and don't count toward costing/badges (D5). A **Review** filter/section jumps to DJ4. |
| 4 | User | Taps a deal → product detail, or taps **Review** to work the queue (DJ4). |

**Domain events emitted:** none (pure query).

**Edge cases:**
- No active deals (no flyers pulled yet, or none in window) → empty-state inviting the user to subscribe to stores (DJ1).
- A deal's window lapses while the page is open → it drops out of "active" on reload; its price history persists (D9).
- An auto-matched deal the user disagrees with → Correct/Reject from here routes into DJ4 (and rewrites memory).

---

### DJ4 — Review the deal queue (Confirm / Correct / Reject)

**Trigger:** User taps Review on the Deals page (§6b), or the Home "N deals ready to review" banner
(§0b, Phase-4 banner). This is the deal-side twin of the Intake review form (§2e), and reuses its
ACL mental model.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Shows the **pending** deals: each row = raw flyer name, brand, price, **sale story**, validity window, and the **AI-proposed match** (suggested product + confidence treatment — `high` pre-filled, `low` flagged, `none`/"Unrecognized", DL-O7). |
| 2 | User | Per deal, chooses one of three verbs (§6b): **Confirm** the proposed match · **Correct** — search the catalog and pick the right product (or an AI-ranked "did you mean" alternative, reusing the Intake chip pattern) · **Reject** — mark irrelevant (not a product we track). |
| 3 | System | On **Confirm/Correct:** the deal flips to `confirmed` with the resolved `product_id`; **upserts `DealMatchMemory(store_id, normalized_name) → product_id`** so this item auto-confirms next flyer (D4); and **writes one `pricing.price_observation`** (`source=deal`, window, `store_id`, `source_ref=deal_id`, D6). |
| 4 | System | On **Reject:** the deal flips to `rejected`; optionally records a **negative** memory (`normalized_name → no product`) so the same junk isn't re-queued. **No** price observation is written (D5). |
| 5 | System | The queue shrinks; when empty, the Home banner clears. Confirmed deals immediately become **active** (if in-window) and light up costing/badges/alerts (D6/D10/D11). |

**Domain events emitted:** `DealConfirmed(householdId, dealId, productId, storeId, validFrom, validTo, by, at)`
on confirm/correct; `DealRejected(householdId, dealId, by, at)` on reject.

**Edge cases:**
- The matched product doesn't exist yet in the catalog → **inline create-product** (reuse the Intake §2d create flow), then confirm against the new product. (Or Reject if it's not worth cataloguing.)
- Correcting an **already-confirmed** auto-matched deal (from DJ3) → re-resolves `product_id`, **rewrites** memory, and **supersedes** the prior `price_observation` with a corrected one (Pricing is append-only — a correction is a new row, R1/DM-17), never an in-place edit.
- Two pending deals normalize to the **same** `(store, normalized_name)` (dup flyer entries) → memory write is idempotent; both resolve to the same product, both observe a price (Pricing tolerates duplicate observations).
- Confirming a deal whose window already **expired** between pull and review → it's confirmed and its observation written (history), but it never shows as "active" (D9).

---

### DJ5 — Stock-up alert → add to shopping list

**Trigger:** A product the household **buys frequently** has an **active confirmed deal** (§6c). The
alert surfaces on the Deals page (banner/badge) and optionally the Home banner stack.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System | Computes the **stock-up read model**: products above a purchase-frequency threshold (DL-O4) intersected with active deals (D9). |
| 2 | System | Surfaces each as an alert — "**Chicken breast** is on sale at FreshCo (you buy this often)" — with the store, price, and valid-until. |
| 3 | User | Taps **Add to shopping list**. |
| 4 | System | Calls **`IShoppingListWriter.AddItems(product_id, qty, unit, source="deal", source_ref=deal_id)`** — the **P2-4 seam, reused** (DM-18); Shopping applies its merge rule. The item then carries a deal badge in Shopping (D11). |

**Domain events emitted:** none (Shopping owns its state; the add is a direct port call).

**Edge cases:**
- A frequently-bought product with **no** active deal → no alert (the intersection is empty).
- The user already has the item on the list → Shopping's merge avoids a duplicate (DM-18).
- Push delivery of the alert is **deferred** (in-app only in v1; web-push needs PWA install — VISION open question, D10).

---

### DJ6 — Deal badge on the shopping list / recipe cost (read-time enrichment)

**Trigger:** The user views their shopping list (§3a/§3f) or a recipe's cost (§4); the deal data
**enriches surfaces other contexts own** — Deals supplies a read model, it renders nothing itself.

| Step | Actor | Action / System response |
|------|-------|--------------------------|
| 1 | System (Shopping) | For the listed products, joins against Deals' **`IActiveDealReader.ForProducts`** at read time; an item with an active deal shows a **deal badge** ("On sale at FreshCo this week"), tappable to deal detail (§3f). **Never stored** on the item (D11 / DM-18). |
| 2 | System (Recipes / Meal Planning) | Cost-per-serving's **deal-aware tier** (SPEC §4) now resolves a non-null "cheapest active deal" from **Pricing** (because Deals writes deal observations, D6). No new Deals port — the seam was already built; it simply stops returning empty. |
| 3 | System (Meal Planning) | The **`Deals` planning lever** (pinned at 0 / hidden in Phase 3, [C7](../MealPlanning/mealplanning-journeys.md)) **un-pins**: the planner can now bias toward ingredients cheap *this week*, fulfilling the VISION "deal-aware planner" pillar. |

**Domain events emitted:** none (pure reads across contexts).

**Edge cases:**
- A product with deals at multiple stores → "cheapest active deal" (Pricing) wins; the badge can name the cheapest store.
- A deal expires mid-session → the badge disappears on the next read; recipe cost falls back to purchase-history pricing (D9).

---

## Cross-Cutting Notes

**Deals is an anticorruption layer over an untrusted, fragile feed (ADR-007/ADR-010).** Flipp has no
official public API, so the `IFlyerSource` adapter is the single brittle seam — isolated in
`Plantry.Deals.Infrastructure`, returning normalized `RawDeal`s the domain can trust-by-shape. The
raw pull is quarantined in `FlyerImport.raw_flyer`; only **user-confirmed (or memory-auto-confirmed)**
deals cross into Pricing (D5). This is the **same review-then-commit discipline as Intake** — and
deliberately the **persisted** ACL (D2), the opposite of Meal Planning's transient pending store,
because flyer review happens **asynchronously, over time**, not in one sitting.

**Deals prices; it never moves stock (D8).** A deal is an *advertised price*, not a purchase.
Confirming a deal writes a `PriceObservation`, never an Inventory journal row. Stock changes only when
the user actually buys and logs it through Intake. The one place Deals touches another context's
mutable state is a **stock-up-alert tap** adding to the shopping list (D10) — and even that reuses the
existing P2-4 write seam.

**Costing "just lights up" — the seam was pre-built (D6).** The hardest integration work was done in
Phase 1–2: Pricing's `price_observation` already carries `source=deal` + a validity window, and
"cheapest active deal" is already a Pricing read model that Recipes and Meal Planning consume. Deals'
job is to **fill that seam** — write deal observations on confirm — after which deal-aware recipe
cost, the shopping deal badge, and the Meal Planning `Deals` lever activate **with no change to those
contexts**. This is the payoff of the DM-16/DM-17 phase-inversion-avoidance decisions.

**Match memory is what makes deals low-friction at steady state (D4).** The first flyer from a new
store is mostly review; after the household confirms its regulars, `DealMatchMemory` auto-confirms the
repeats, so each subsequent week's queue is just the *new* items. The memory key
`(store_id, normalized_name)` is deterministic and reproducible (DL-O6) — never AI-derived — so it
stays stable across pulls. This mirrors the SPEC §6b promise: "the same item doesn't need review
again."

**The human is authoritative, always.** AI confidence shapes the *review treatment* but never
auto-confirms on its own (ADR-007); memory auto-confirms only what the human previously confirmed
(D4). Every auto-matched deal stays Correctable/Rejectable (DL-O3), and every correction rewrites
memory and supersedes the price observation (append-only, never edited). Deals proposes; the household
disposes — the same minimum-friction principle that governs Intake and Meal Planning.

**Retention as price history (D9).** Expired and rejected deals are **never deleted**. A confirmed
deal's `price_observation` lives in Pricing indefinitely (SPEC §6), so this week's sale price becomes
next quarter's cost-trend datapoint. This is why Deals writes *through* Pricing rather than holding
its own ephemeral price table — the append-only log is the long-term value.
