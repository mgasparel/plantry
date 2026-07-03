# Deals — Ubiquitous Language

> **Status:** Complete (approved) — Phase 5. Second stage of the Deals design chain. Every term here should appear
> **verbatim** in domain code, schema, and conversation. Built from
> [deals-journeys.md](deals-journeys.md) and aligned with the established `DataModels/`, Intake, and
> Pricing vocabulary. Feeds the Domain Model (next step).
>
> **Bounded context:** Deals (`deals` schema, Phase 5). A **core** context wrapping an untrusted
> external flyer feed behind an anticorruption layer (ADR-007/ADR-010). References Catalog, Pricing,
> Shopping, Inventory, Identity **by ID only** (DM-3); writes `deal` price observations to Pricing on
> confirm. The `catalog.store` reference table lands this phase (Catalog-owned, DM-16).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Naming Decisions

| # | Concept | Term | Rationale |
|---|---------|------|-----------|
| N1 | The merchant *identity* (a store the household can pull from / a deal is at) | **`Store`** (Catalog-owned) | Stable per-household reference data, same shape as `Location`/`Unit`/`Category` ([DM-16](../../DataModels/index.md)). Lives in `catalog`, referenced here by `StoreId`. **Not** a Deals aggregate — Deals references it, never owns it (D7). |
| N2 | The household's choice to pull flyers from a store | **`StoreSubscription`** | The Deals-owned *config* (§7e "manage which stores to pull deals from"), distinct from the `Store` identity (D7). "Subscription," not "StoreConfig," to read as the ongoing pull relationship. |
| N3 | One pull of one store's flyer (the ACL envelope + provenance) | **`FlyerImport`** | The async-pull unit: raw payload quarantine + pull status + dedup anchor. Mirrors Intake's `ImportSession`/`import_receipt` *role* (D2/DL-O2). "Flyer**Import**," echoing "Import"Session, signals the shared ACL lineage. |
| N4 | A single normalized flyer item, pre-match (stage 1 output) | **`RawDeal`** | The transient, quarantined item the `IFlyerSource` adapter yields per flyer line (D3). "Raw" marks it as untrusted, un-reviewed input — the deal-side analog of an Intake stage-1 line item. Never a persisted aggregate; materialized into a `Deal`. |
| N5 | A reviewable / confirmable deal record | **`Deal`** | The first-class, long-lived record: raw + normalized fields, the resolved match, `status`, validity window (DL-O1). What the user browses (§6a), reviews (§6b), and what projects a price observation. |
| N6 | The remembered match that skips re-review | **`DealMatchMemory`** | Keyed `(StoreId, NormalizedName) → ProductId` (SPEC §6b step 4, D4). "Memory," matching SPEC's own word, not "MatchCache" — it's a learned, durable mapping, not a transient cache. |
| N7 | The deterministic match/dedup key derived from a flyer item's name | **`NormalizedName`** | A reproducible normalization of the flyer item name (lowercase, trim, strip pack-size/units/punctuation), computed in the ACL — **never AI-derived** (DL-O6). The stable half of the `DealMatchMemory` key. |
| N8 | How sure the auto-match is | **`MatchConfidence`** | Reuses Intake's **`High` / `Low` / `None`** scale verbatim (DM-15, DL-O7) so the review-form treatment and "show low-confidence only" filter carry over. Shapes review treatment; **never** auto-confirms (only `DealMatchMemory` does, D4/D5). |
| N9 | The flyer's human phrasing of the offer | **`SaleStory`** | e.g. "2 for $5", "Save $1.50", "Buy 1 Get 1". Free-text provenance shown in review (§6b) and on the deal; the structured `price`/`quantity` are what normalize into `unit_price`. "Story," not "Description," to mark it as the as-advertised narrative. |
| N10 | The dates a deal is good for | **`ValidityWindow`** (`ValidFrom` / `ValidTo`) | The flyer's start/end dates (D9). Drives "**Active**" and projects directly onto `price_observation.valid_from`/`valid_to` (DM-17). Same column names as Pricing, deliberately. |

---

## Aggregates & Entities

| Term | Kind | Definition |
|------|------|------------|
| **StoreSubscription** | Aggregate root (one per `(household, store)`) | The household's standing choice to pull flyers from a `Store` (N2/D7). Holds the `StoreId` (→ `catalog.store`), an active/paused flag, and last-pull bookkeeping. Mutable: subscribe / unsubscribe. The async worker (DJ2) iterates active subscriptions. |
| **FlyerImport** | Aggregate root (ACL provenance) | One pull of one store's flyer (N3/D2). Holds the **raw pull payload** in `raw_flyer` jsonb (the quarantine half of the ACL), the flyer `ValidityWindow`, a dedup key (`flyer_external_id` / content hash, DL-O5), and a **pull `status`** (`Pulling` / `Parsed` / `Failed`, with `error_detail`). The async-worker unit and de-dup anchor; spawns `Deal`s, which soft-ref it. Provenance is **retained**, never deleted (R-style, audit). |
| **Deal** | Aggregate root | One normalized, reviewable deal (N5/DL-O1). Carries the raw flyer fields (name, brand, size, price, `SaleStory`), the derived `NormalizedName`, the quarantined match proposal (`suggested_product_id` + `MatchConfidence` + reasoning), the **user-resolved `ProductId`**, a `DealStatus`, the `ValidityWindow`, and a soft-ref to its `FlyerImport`. Its **own** root (not a `FlyerImport` child) because it has an independent, long-lived lifecycle — browsed while active, feeding Pricing across its window, expiring on its own clock (DL-O1). |
| **DealMatchMemory** | Aggregate root (one per `(household, store, normalized_name)`) | The remembered resolution `(StoreId, NormalizedName) → ProductId` (N6/D4). Upserted on Confirm/Correct; optionally a **negative** memory (`→ no product`) on Reject (DJ4 step 4). The auto-confirm key on the next pull. Small, flat, durable. |

> **Four aggregates, not "Store + Deal + DealMatchMemory" (ADR-010's three).** ADR-010 listed
> `Store`, `Deal`, `DealMatchMemory`. This refines it: (a) the **merchant identity** `Store` is
> **Catalog-owned** (DM-16), so Deals' configuration counterpart is **`StoreSubscription`**; and
> (b) a **`FlyerImport`** provenance/ACL envelope is added (the persisted-staging decision, D2/DL-O2)
> — ADR-010 folded "flyer ingestion" into `Deal` but the async, batched, retryable pull earns its own
> root. To be recorded as an ADR-010 amendment when the domain model lands.

---

## Value Objects (computed or transient, not stored unless noted)

| Term | Definition |
|------|------------|
| **RawDeal** | The transient, quarantined stage-1 item the `IFlyerSource` adapter yields per flyer line (N4): item name, brand, size, price, `SaleStory`, `ValidityWindow`. Untrusted by construction; **not persisted as itself** — it is normalized and materialized into a `Deal` (its raw form survives only inside `FlyerImport.raw_flyer`). The deal-side twin of an Intake stage-1 line item. |
| **NormalizedName** | The deterministic, reproducible normalization of a flyer item's name (N7/DL-O6). Computed in the ACL, stable across pulls; the match-memory and dedup key. Never AI-derived. |
| **MatchConfidence** | Enum **`High` / `Low` / `None`** (N8, reused from Intake DM-15). The AI/heuristic match's self-assessment; drives review-form treatment only. |
| **DealStatus** | Enum **`Pending`** (awaiting review) · **`Confirmed`** (resolved match, written to Pricing — eligible to be active) · **`Rejected`** (not a tracked product; no price observation). Monotonic in the common path; a `Confirmed` auto-match may be **Corrected** (re-resolves product, supersedes the observation) or **Rejected** (DJ3/DJ4 edge cases). |
| **ValidityWindow** | `ValidFrom` / `ValidTo` dates (N10/D9). A deal is **Active** when `ValidFrom ≤ today ≤ ValidTo` **and** `DealStatus = Confirmed`. Projects onto `price_observation.valid_from`/`valid_to`. |
| **PullStatus** | Enum on `FlyerImport`: **`Pulling`** · **`Parsed`** · **`Failed`** (+ `error_detail`). The async-ingestion lifecycle (DJ2), mirroring Intake's session `status`. |
| **ActiveDeal** (read model) | A `Deal` that is `Confirmed` **and** in-window, projected per product — powers the **Deals page** (§6a) and stock-up alerts, **in-context**. **Not exposed to Shopping** (ADR-010): the deal badge (D11) and deal-aware cost read **Pricing's** cheapest-active-deal read model. Read-side, not stored; recomputed from `Deal` + clock. |
| **StockUpAlert** (read model) | One advisory alert (D10/§6c): a **frequently-bought** product (purchase-frequency over threshold, DL-O4) that currently has an **ActiveDeal**. Read-side, recomputed; never stored. Carries the product, the cheapest active deal's store + price, and the validity window. |

---

## The ACL, match memory & confirm (the heart of the context)

Deals wraps an **untrusted, fragile external feed** (Flipp) behind an anticorruption layer
(ADR-007/ADR-010). The core move, per flyer pull (DJ2):

1. The **`IFlyerSource`** adapter (Infrastructure) pulls the flyer and yields normalized **`RawDeal`s**
   — the domain never sees a Flipp DTO. The raw payload is quarantined in **`FlyerImport.raw_flyer`**.
2. Each `RawDeal` gets a deterministic **`NormalizedName`** and is looked up in **`DealMatchMemory`**
   `(StoreId, NormalizedName)`. **Remembered ⇒ auto-confirm** (`High` confidence, skips the queue,
   D4); **unremembered ⇒ AI/heuristic match** → `suggested_product_id` + **`MatchConfidence`**,
   landing **`Pending`** for review.
3. A **`Deal`** is materialized per `RawDeal`. Only **confirmed** deals (auto- or user-) cross the
   boundary: each writes **one `pricing.price_observation`** (`source=deal`, `ValidityWindow`,
   `store_id`, `source_ref=deal_id`, D6) and **upserts `DealMatchMemory`**.
4. **Confirm / Correct / Reject** (§6b) are the human review verbs. Confidence shapes the *treatment*;
   it never auto-confirms — **only memory does** (D5). A correction **rewrites memory** and
   **supersedes** the price observation (Pricing is append-only; a correction is a new row, never an
   edit, DM-17/R1).

This is the **same review-then-commit discipline as Intake**, and deliberately the **persisted** ACL
(D2) — the opposite of Meal Planning's transient pending store — because flyer review is
**asynchronous and spread over time** (the queue waits for the user, §0b), not done in one sitting.

**Deals prices, never stock (D8).** Confirming a deal writes a *price observation*, never an Inventory
journal row — a deal is an advertised price, not a purchase. The only mutable cross-context write
besides Pricing is a **stock-up-alert tap** adding to the shopping list (D10), via the reused P2-4
seam.

---

## Domain Events

| Event | Payload | Emitted when |
|-------|---------|--------------|
| **FlyerImported** | `householdId, flyerImportId, storeId, pendingCount, at` | A flyer pull finishes parsing (DJ2). Drives the Home "N deals to review" banner (§0b / [plantry-bpw]). |
| **DealConfirmed** | `householdId, dealId, productId, storeId, validFrom, validTo, by, at` | A deal is confirmed/corrected — by the user (DJ4) or auto via memory (DJ2). The trigger for stock-up-alert recomputation and the audit trail. `by` is null/system for memory auto-confirm. |
| **DealRejected** | `householdId, dealId, by, at` | A deal is rejected in review (DJ4). |

> Kept deliberately light, as Recipes / Meal Planning did. In Phase 5 these primarily feed the Home
> banner and audit/attribution; stock-up alerts are a **read model** recomputed on demand, so they do
> not *require* an event subscriber (an event-driven refresh is an optimization, not a dependency).
> `DealConfirmed` is the natural hook if push notifications (deferred, D10) are ever added.

---

## Key Actions (verbs)

| Verb | Meaning |
|------|---------|
| **Subscribe** / **Unsubscribe** | Add/remove a `StoreSubscription` (which merchants to pull from, §7e / DJ1). Subscribing ensures the `catalog.store` identity exists. |
| **Pull** / **Ingest** | The async worker fetches a store's flyer via `IFlyerSource`, creating/updating a `FlyerImport` and its `Deal`s (DJ2). |
| **Normalize** | Stage-1: turn the raw flyer into `RawDeal`s with a `NormalizedName` (DJ2). |
| **Match** | Stage-2: resolve a `RawDeal` to a `catalog.product` — memory first, else AI/heuristic with a `MatchConfidence` (DJ2). |
| **Auto-confirm** | A memory-matched deal lands `Confirmed` without review (D4), still Correctable/Rejectable (DL-O3). |
| **Review** | Work the pending queue: **Confirm** / **Correct** / **Reject** (§6b / DJ4). |
| **Confirm** | Resolve a deal's `ProductId`, upsert `DealMatchMemory`, and write its `deal` `PriceObservation` (D6 / DJ4). |
| **Correct** | Re-resolve the product (catalog search / "did you mean"), rewrite memory, supersede the observation (DJ4). |
| **Reject** | Mark a deal not-a-tracked-product; no price observation; optional negative memory (DJ4). |
| **Browse** | View active deals and pending ones (§6a / DJ3). |
| **Add to list** | From a stock-up alert, add the product to the shopping list via the reused `IShoppingListWriter` seam (§6c / DJ5). |

---

## Cross-context terms (owned elsewhere, referenced by ID)

These are **not** redefined here — this fixes which word Deals uses for each.

| Term | Owning context | Note |
|------|----------------|------|
| **Store** | Catalog (`catalog.store`, DM-16) | The merchant identity. Deals references `StoreId`; its **table lands this phase** but it is Catalog-owned reference data (D7/N1). Subscribing ensures the row exists. |
| **Product** | Catalog (DM-10) | A `Deal` resolves to a `ProductId` on confirm; inline create-product reuses the Intake §2d flow (DJ4). Read for name/metadata in browse & review. |
| **PriceObservation** | Pricing (DM-17) | Deals' single downstream **write** on confirm (`source=deal`; the seam designed in DM-17, **built in P5-P**, D6). "Cheapest active deal" / "latest price" are **Pricing** read models — Deals owns no costing math. |
| **CostPerServing** / deal-aware tier | Recipes (read model) | Reads **Pricing's** cheapest-active-deal read model; **no new Deals port**. Not automatic — the read model is built (P5-P) and the C7 deal-blind cost reader wired (P5-9b) this phase. |
| **PlanningLever `Deals`** | Meal Planning (C7/C14) | The lever pinned at 0 in Phase 3 **un-pins** by wiring the C7 deal-blind price reader to Pricing (P5-9b). No Deals code — it reads Pricing. |
| **ShoppingList** / **AddItems** | Shopping (DM-18) | Target of a stock-up-alert "add to list" (D10/DJ5), reusing the P2-4 seam. The deal **badge** is a Shopping read-time join against **Pricing's cheapest-active-deal read model** (D11/§3f, ADR-010) — Shopping reads Pricing, never Deals. |
| **Purchase frequency** | Inventory journal (`Purchase` rows) **or** Pricing purchase observations | The stock-up "buys this often" signal (DL-O4), read via `IPurchaseFrequencyReader`; owning context settled in the model/app pass. |
| **Household** / **User** | Identity (DM-6) | Tenancy; `by` attribution on review actions and events. |
| **AI / household AI key** | per ADR-007 / DM-7 | The stage-2 matcher is an **untrusted function** wrapping the `ChatClient` over the encrypted per-household key, exactly as Intake's matcher — key never client-sent. |
| **The flyer feed (Flipp)** | external (via `IFlyerSource`, Infrastructure) | The untrusted, fragile source (D1). Isolated behind the adapter; the domain sees only `RawDeal`s. |

---

## Reconciliation notes

- **ADR-010 "Deals — `Store`, `Deal`, `DealMatchMemory`" — refined.** (a) `Store` (merchant
  identity) is **Catalog-owned** (DM-16), so Deals' config counterpart is **`StoreSubscription`**
  (N2); (b) a **`FlyerImport`** provenance/ACL aggregate is added for the **persisted, async**
  flyer pull (D2/DL-O2) — ADR-010 folded ingestion into `Deal`; (c) `Deal` is its **own root** with
  an independent active/expired lifecycle (DL-O1), not a child of the pull envelope (the divergence
  from Intake's `ImportSession`/`ImportLine`); (d) `DealMatchMemory` is confirmed as-is, with an
  optional **negative** memory on Reject (DJ4). **Record as an ADR-010 amendment when the domain
  model lands.**

- **ADR-010 "Deals ACL specifics (tied to the unresolved Flipp access question)" — resolved.** The
  Flipp access question (VISION open questions) is settled to the **unofficial API / scraper behind an
  `IFlyerSource` adapter** (D1); the ACL is the **persisted** `FlyerImport` + `Deal` staging with
  match memory (D2/D3/D4), mirroring Intake (ADR-007) rather than Meal Planning's transient store.

- **DM-16 finalized in practice.** `catalog.store` now exists (this phase), so `price_observation`'s
  nullable `store_id` soft-ref becomes populated for deal observations and **back-fillable** for
  historical purchase observations — closing the DM-16 deferral.

- **Phase numbering.** Deals is **build-phase 5** (P4 is Take Stock — inventory reconciliation).
  Older docs that numbered Deals earlier (Phase 3, then Phase 4 before Take Stock was injected) are
  **reconciled in the schema pass** — see the [ADR-010](../../ADRs/ADR-010.md) amendment 2026-06-22.
