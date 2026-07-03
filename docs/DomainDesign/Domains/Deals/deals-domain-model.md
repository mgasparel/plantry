# Deals — Domain Model

> **Status:** Draft — pending approval — Phase 5. Modeling calls DL-O1–DL-O7 resolved here. Rendered into the
> **Data Schema** ([`DataModels/deals.md`](../../DataModels/deals.md), **DM-22**); the remaining step is a
> delivery plan (PHASE-5-PLAN, not written yet).
>
> **Purpose:** Translate the confirmed [ubiquitous language](deals-ubiquitous-language.md) into
> aggregate boundaries, invariants, behaviours, value objects, and the cross-context ports the Deals
> context needs. This is the contract the Data Schema step renders into the `deals` schema and the App
> Services step implements. Terms here appear **verbatim** in the language doc.
>
> **Bounded context:** Deals (`deals` schema, Phase 5). A **core** context wrapping an untrusted,
> fragile external flyer feed (Flipp) behind an anticorruption layer (ADR-007/ADR-010). References
> Catalog, Pricing, Shopping, Inventory, Identity **by ID only** — no enforced cross-context FKs
> (DM-3). The `catalog.store` reference table lands this phase (Catalog-owned, DM-16).
>
> **Code shape:** aggregates follow the established pattern — `AggregateRoot<TId>` with strongly-typed
> IDs, private setters, factory `Create`/`Start`, `IClock`-stamped mutators, `Result<T>`/`Error` for
> failable operations (see `Plantry.Inventory.Domain.ProductStock`,
> `Plantry.Intake.Domain.ImportSession`). The ACL **mirrors Intake's persisted staging** intent
> (review-then-commit, raw payload quarantined as jsonb) — and deliberately **not** Meal Planning's
> transient store (D2/MP-O7): flyer review is async and spread over time.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## 1. Context boundary & dependency rules

Deals is a **core** context that turns an untrusted flyer feed into trusted `deal` price observations.
It owns **four** aggregates — `StoreSubscription`, `FlyerImport`, `Deal`, `DealMatchMemory` — and
reaches everything else through ports (§8). Its single downstream **write** in the steady state is one
`price_observation` per confirmed deal (the Pricing seam — designed DM-17, built in P5-P, D6); its only other mutable
cross-context write is a stock-up-alert "add to shopping list" (D10).

| Rule | Statement |
|---|---|
| **Ownership** | Deals owns `StoreSubscription`, `FlyerImport`, `Deal`, `DealMatchMemory`. It does **not** own `Store` — the merchant *identity* is Catalog reference data (DM-16/D7), referenced by `StoreId`. |
| **Reference by ID** | `store_id`, `product_id`, the price-observation target, the shopping `AddItems` target — all soft-refs across context boundaries, never object graphs, never cross-schema FKs (DM-3). |
| **AI & the feed are untrusted (ADR-007)** | The Flipp feed (`IFlyerSource`) and the matcher (`IDealMatcher`) are untrusted external functions. Their output is quarantined in `FlyerImport.raw_flyer` / `Deal`'s `suggested_*` fields and **only user-confirmed (or memory-auto-confirmed) deals cross into Pricing** (D5). |
| **Persisted ACL (D2)** | Unlike Meal Planning's transient pending store, the staging is **durable**: the raw pull and the proposed matches survive sessions because flyer review is async (§0b). This is the Intake `ImportSession` shape, not the MealPlanning one. |
| **Deals prices, never stock (D8)** | Confirming a deal **never** writes Inventory — a deal is an advertised price, not a purchase. The only Inventory contact is a **read** (purchase frequency, for stock-up alerts). |
| **The human is authoritative** | `MatchConfidence` shapes review treatment but never auto-confirms; only `DealMatchMemory` (a prior human decision) auto-confirms (D4/D5). Auto-matched deals stay Correctable/Rejectable (DL-O3); a correction rewrites memory and **supersedes** the observation (append-only, never edited). |
| **Same-context FKs allowed** | `FlyerImport`↔`Deal` and `StoreSubscription`/`DealMatchMemory` live in `deals`, so cross-aggregate refs *may* use real FKs (DM-3 permits hard FKs within a context). `Deal.flyer_import_id` is the one enforced within-context cross-aggregate FK (RESTRICT; nullable for the deferred manual-entry path, D12). |

---

## 2. Aggregate map

| Aggregate root | Identity | Owns (composition) | References by ID | Lifecycle |
|---|---|---|---|---|
| **StoreSubscription** | `StoreSubscriptionId` (unique per `(household, store)`) | — (flat) | `HouseholdId`, `StoreId` (→ `catalog.store`) | Mutable; subscribe / pause / unsubscribe (soft) |
| **FlyerImport** | `FlyerImportId` | — (flat; raw payload as jsonb) | `HouseholdId`, `StoreId` | Created per pull; `Pulling → Parsed`/`Failed`; **retained** (provenance) |
| **Deal** | `DealId` | — (flat) | `HouseholdId`, `FlyerImportId` (within-context FK), `StoreId`, resolved `ProductId`, `SuggestedProductId`, `UnitId?`, `CommittedPriceObservationId` | `Pending → Confirmed`/`Rejected`; **retained** as price history (D9) |
| **DealMatchMemory** | `DealMatchMemoryId` (unique per `(household, store, normalized_name)`) | — (flat) | `HouseholdId`, `StoreId`, `ProductId?` (null = negative memory) | Mutable; upserted on confirm/correct/reject |

> **Four aggregates, flat by design.** None has child entities — Deals is a set of related but
> independently-lived records (a subscription, a pull, a deal, a remembered match), not a deep
> composition. This **diverges from Intake** (`ImportSession`→`ImportLine` children) precisely because
> a `Deal` is **its own long-lived root** (DL-O1), not a review row inside a session. `FlyerImport` is
> a thin **provenance/ACL envelope** (DL-O2), not a parent owning the deals — `Deal`s reference it by
> a within-context FK and outlive its relevance.

---

## 3. StoreSubscription aggregate

The household's standing choice to pull flyers from a `catalog.store` (§7e / DJ1). One per
`(household, store)`. The **postal code lives on the subscription** — Flipp's feed is postal-code-scoped
(you fetch flyers *near a postal code* and filter by merchant name; there is no stable store-directory
lookup), so location is captured on the Deals subscription page, **not** as a household-global setting.

### 3.1 StoreSubscription (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `StoreSubscriptionId` | |
| `HouseholdId` | `HouseholdId` | tenancy |
| `StoreId` | `StoreId` | soft-ref → `catalog.store`; **unique per household** (DD9) |
| `PostalCode` | `string` | the location the flyer is pulled for (Flipp `/data?postal_code=…`); captured at subscribe (§7e). A household typically uses one, but it is per-subscription so a merchant in a different area still resolves |
| `IsActive` | `bool` | paused subscriptions are skipped by the worker but retained (with their match memory) |
| `LastPulledAt` | `DateTimeOffset?` | bookkeeping for the worker / UI "last updated" |
| `LastFlyerExternalId` | `string?` | the last pulled flyer's external id — the dedup anchor (DD5/DL-O5) |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | |

**Behaviours**

| Method | Effect |
|---|---|
| `StoreSubscription.Subscribe(householdId, storeId, postalCode, clock)` | Factory. Starts active. (Caller ensures the `catalog.store` identity exists first — §8.) |
| `Pause(clock)` / `Resume(clock)` | Toggle `IsActive` without losing history/memory. |
| `Unsubscribe(clock)` | Soft-deactivate (sets `IsActive = false`); existing confirmed deals + price history + match memory are retained (D9). |
| `RecordPull(flyerExternalId, clock)` | Stamps `LastPulledAt` / `LastFlyerExternalId` after a successful pull. |

---

## 4. FlyerImport aggregate (the ACL provenance envelope)

One pull of one store's flyer (DJ2 / D2 / DL-O2). The async-worker unit, the raw-payload quarantine,
and the de-dup anchor. **Retained** — never deleted (audit, like Intake's `import_receipt`).

### 4.1 FlyerImport (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `FlyerImportId` | |
| `HouseholdId` | `HouseholdId` | tenancy |
| `StoreId` | `StoreId` | which store's flyer |
| `FlyerExternalId` | `string` | Flipp's flyer id; with `(household, store)` the **dedup key** (DD5) |
| `ContentHash` | `byte[]?` | sha256 of the raw payload — secondary dedup (a re-pull of identical bytes is a no-op, DL-O5; mirrors `import_receipt.sha256`) |
| `ValidityWindow` | `(DateOnly ValidFrom, DateOnly ValidTo)` | the flyer's run dates; copied onto each `Deal` (D9) |
| `RawFlyer` | `jsonb` | the **full raw pull payload** — the ACL quarantine; **never overwritten** after parse (DD6). Opaque to the domain |
| `Status` | `PullStatus` | `Pulling` / `Parsed` / `Failed` (DD12) |
| `ErrorDetail` | `string?` | set when `Failed` (Flipp unreachable / parse error, DJ2 edge) |
| `PulledAt` | `DateTimeOffset` | when the pull ran |
| `ParsedAt` | `DateTimeOffset?` | when normalization+match finished (`status → Parsed`) |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | |

**Behaviours:** `FlyerImport.Start(householdId, storeId, flyerExternalId, contentHash, window, rawFlyer, clock)`
(factory, `status: Pulling`); `MarkParsed(clock)`; `MarkFailed(error, clock)`. The `RawFlyer` payload
is set once at `Start` and is immutable thereafter (DD6).

> `FlyerImport` holds **no** typed deal rows — those are separate `Deal` roots (§5). It is pure
> provenance + pull lifecycle. This is the deliberate split from Intake, where `ImportLine`s are
> children of the session (DL-O1/O2).

---

## 5. Deal aggregate

One normalized, reviewable deal (DL-O1). Its **own root** — browsed while active, feeding Pricing
across its window, expiring on its own clock — not a child of `FlyerImport`. **Retained** as price
history (D9).

### 5.1 Deal (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `DealId` | |
| `HouseholdId` | `HouseholdId` | tenancy |
| `FlyerImportId` | `FlyerImportId?` | within-context FK → `FlyerImport` (RESTRICT); **null** only for the deferred manual-entry path (D12) |
| `StoreId` | `StoreId` | soft-ref → `catalog.store` (denormalized from the import for query/read models) |
| `Source` | `DealSource` | `flyer` (v1) / `manual` (deferred, D12) — discriminator left in the model, only `flyer` built |
| **— Raw flyer fields (ACL, read-only after parse) —** | | |
| `RawName` | `string` | the item as advertised — the review row's anchor |
| `Brand` | `string?` | |
| `Size` | `string?` | the advertised pack/size text |
| `Price` | `numeric(12,2)` | the advertised sale price for `Quantity` |
| `Quantity` | `numeric(12,3)?` | pack size the price is for (for `unit_price` normalization in Pricing) |
| `UnitId` | `UnitId?` | soft-ref → `catalog.unit`; the unit of `Quantity` (resolved in the ACL where possible) |
| `SaleStory` | `string?` | "2 for $5" / "Save $1.50" — free-text provenance (N9) |
| `NormalizedName` | `string` | deterministic key (DD4/DL-O6); with `StoreId` the `DealMatchMemory` key |
| **— Match proposal (ACL quarantine, never overwritten) —** | | |
| `SuggestedProductId` | `ProductId?` | the matcher's pick (memory or AI); read-only provenance (DD6) |
| `MatchConfidence` | `MatchConfidence` | `High` / `Low` / `None` (DD6) |
| `MatchReasoning` | `string?` | the AI's rationale (or "remembered match"); provenance |
| **— User-resolved (the only field that commits) —** | | |
| `ProductId` | `ProductId?` | the resolved match; set on Confirm/Correct (auto-confirm copies `SuggestedProductId`). Null while `Pending` or after `Rejected` |
| **— Lifecycle & linkage —** | | |
| `Status` | `DealStatus` | `Pending` / `Confirmed` / `Rejected` (DD1) |
| `ValidityWindow` | `(DateOnly ValidFrom, DateOnly ValidTo)` | from the flyer; **Active** = `Confirmed` ∧ in-window (DD7) |
| `CommittedPriceObservationId` | `PriceObservationId?` | soft-ref → `pricing.price_observation`; the row this deal projected on confirm (DD2) |
| `AutoMatched` | `bool` | true if memory auto-confirmed it (drives the "auto-matched" marker, DL-O3) |
| `ReviewedByUserId` | `UserId?` | who confirmed/corrected/rejected; null for memory auto-confirm |
| `ReviewedAt` | `DateTimeOffset?` | |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | |

**Behaviours**

| Method | Effect |
|---|---|
| `Deal.Stage(householdId, flyerImportId, storeId, rawFields, normalizedName, matchProposal, window, clock)` | Factory. Materializes a `Pending` deal from a `RawDeal` + its match proposal (DJ2 step 5). |
| `AutoConfirm(productId, clock)` | Memory path (D4): sets `ProductId = productId`, `Status = Confirmed`, `AutoMatched = true`, `MatchConfidence = High`. The price-observation write + memory upsert are the **service's** job (§7) — the root only owns its own state. |
| `Confirm(productId, by, clock)` | User confirms the (possibly corrected) match: `ProductId = productId`, `Status = Confirmed`, stamps `ReviewedBy/At`. Permitted even when the window has **closed** — confirming an expired deal is an explicit price-history backfill (DD14); the memory upsert + observation still run. |
| `Correct(productId, by, clock)` | Re-resolve to a different product. Valid on a `Pending` **or** an already-`Confirmed` auto-match (DJ4 edge); flips/keeps `Confirmed` with the new `ProductId`. The supersede-observation + memory-rewrite are the service's job. |
| `Reject(by, clock)` | `Status = Rejected`, `ProductId = null`. Writes no observation (D5). |
| `LinkObservation(priceObservationId, clock)` | Records `CommittedPriceObservationId` after the service writes the Pricing row (DD2). |

> The root keeps its **own** state pure; cross-aggregate effects (write a `PriceObservation`, upsert
> `DealMatchMemory`) are orchestrated by the application service (§7), each its own transaction —
> exactly Intake's commit-orchestration discipline.

### 5.2 RawDeal (transient value object — not persisted as itself)

`{ RawName, Brand, Size, Price, Quantity, UnitId?, SaleStory, ValidityWindow }` — the `IFlyerSource`
adapter's stage-1 output per flyer item (N4). Normalized into a `NormalizedName` and materialized into
a `Deal`; its raw form survives only inside `FlyerImport.raw_flyer`. The deal-side twin of an Intake
stage-1 line item.

---

## 6. DealMatchMemory aggregate

The remembered resolution that skips re-review (D4 / SPEC §6b). One per
`(household, store, normalized_name)`.

> **Store-scoped by design (DD3).** The key includes `StoreId` rather than being household-global
> because the same `NormalizedName` can resolve to **different products across stores** for brand-less
> generics ("2% milk", "bananas") — and the cost of a wrong key is asymmetric. A household-global key
> would risk a **silent cross-store mis-auto-confirm** (memory auto-confirms the wrong product →
> `AutoConfirm` writes a wrong `price_observation`), exactly the silent auto-error the
> human-authoritative posture (DL-O3/C12) exists to prevent. A store-scoped key's only cost is
> re-review per store for identically-named generics — **visible, bounded, and self-correcting**.
> Negative memory is store-scoped on the same grounds. **If multi-store re-review friction ever proves
> real, the fix is a UI "apply to all my stores?" affordance** (writes one memory row per subscription),
> **not** a key change.

### 6.1 DealMatchMemory (root)

| Field | Type | Notes |
|---|---|---|
| `Id` | `DealMatchMemoryId` | |
| `HouseholdId` | `HouseholdId` | tenancy |
| `StoreId` | `StoreId` | soft-ref → `catalog.store` |
| `NormalizedName` | `string` | with `StoreId`, **unique per household** (DD3) |
| `RawName` | `string` | the raw advertised name this key was derived from — **retained** so a normalizer change can re-derive the key without waiting for the item to reappear (DD4) |
| `NormalizerVersion` | `int` | the `DealNormalizer` version that produced `NormalizedName`; a bump flags rows for backfill (DD4) |
| `ProductId` | `ProductId?` | the remembered product; **null = negative memory** ("not a tracked product", DJ4 step 4) |
| `LastConfirmedByUserId` | `UserId?` | provenance |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | |

**Behaviours:** `DealMatchMemory.Remember(householdId, storeId, normalizedName, productId, by, clock)`
(upsert — positive); `RememberNegative(...)` (sets `ProductId = null`); `Repoint(productId, by, clock)`
(a correction rewrites the mapping); `Forget(clock)`. Lookups are by `(StoreId, NormalizedName)`.

---

## 7. Domain & application services

None lives *on* an aggregate — keeping the roots pure of Flipp/AI/Pricing knowledge.

| Service | Responsibility | Touches |
|---|---|---|
| **DealNormalizer** (domain, pure) | `Normalize(rawName) → NormalizedName` — the deterministic, reproducible normalization (lowercase, trim, strip pack-size/units/punctuation, DL-O6). Pure function; the stable match-memory/dedup key. No I/O. | — |
| **IngestFlyer** (application) | DJ2, the async worker. Per active `StoreSubscription`: pull via `IFlyerSource`; **dedup** on `(store, flyer_external_id)` + content hash (DD5) — unchanged ⇒ no-op; create/update a `FlyerImport` (`raw_flyer` quarantined); stage-1 `RawDeal`s + `DealNormalizer`; stage-2 **match** (memory lookup first, else `IDealMatcher` AI); materialize `Deal`s (memory matches → `AutoConfirm` → the confirm side-effects below; rest → `Pending`) — on a re-pull, refresh **only still-`Pending`** deals; resolved deals are frozen (DD13); `MarkParsed`; emit **FlyerImported**. On pull failure → `MarkFailed`, retry next cycle. | `IFlyerSource`, `IDealMatcher`, `ICatalogProductReader`, `IPriceObservationWriter`, in-context (`DealNormalizer`, all four roots) |
| **ConfirmDeal** (application) | DJ4. `Deal.Confirm/AutoConfirm/Correct` → then, **each in its own transaction** (Intake discipline): (1) **upsert `DealMatchMemory`** `(store, normalized_name) → product`; (2) `IPriceObservationWriter.RecordObservation(source=deal, price, quantity, unit, valid_from, valid_to, store_id, source_ref=deal_id)` → `Deal.LinkObservation`. A **Correct** on an already-confirmed deal **supersedes** with a new observation (Pricing append-only, never edits, DM-17/R1) and **Repoints** memory. | `IPriceObservationWriter`, in-context (`Deal`, `DealMatchMemory`) |
| **RejectDeal** (application) | DJ4. `Deal.Reject`; optional `DealMatchMemory.RememberNegative` (DL-O3). Writes no observation. | in-context |
| **ManageSubscriptions** (application) | DJ1. Subscribe/pause/unsubscribe; on subscribe, **ensure** the `catalog.store` identity exists (`ICatalogStoreReader`/`Writer`) then create the `StoreSubscription`. Store-directory search via `IFlyerSource`. | `ICatalogStoreReader`, `ICatalogStoreWriter`, `IFlyerSource`, in-context |
| **BrowseDeals** (application/read) | DJ3 / §6a. Lists **active** deals (`Confirmed` ∧ in-window) and the **pending** queue (`Pending` ∧ in-window — expired-unreviewed deals drop off, DD14), with product names (`ICatalogProductReader`). | `ICatalogProductReader`, in-context |
| **StockUpAlerts** (domain/read + application) | DJ5 / §6c. `Compute() → StockUpAlert[]`: frequently-bought products (`IPurchaseFrequencyReader`, DL-O4) ∩ active deals; recomputed, never stored. "Add to list" → `IShoppingListWriter.AddItems(source="deal", source_ref=deal_id)` (reused P2-4 seam). | `IPurchaseFrequencyReader`, `IShoppingListWriter`, in-context |
| **ActiveDeal** (read model, **in-context**) | Confirmed ∧ in-window deals projected per product, over `Deal` + clock — powers the **Deals page** (§6a) and stock-up alerts. **Not exposed cross-context** (ADR-010): the Shopping badge and Recipes/Meal-Planning cost read **Pricing's** cheapest-active-deal read model, not Deals. Nothing stored. | in-context |

**Commit orchestration (note for the schema/app step).** `ConfirmDeal` mirrors Intake: the
state-flip, the memory upsert, and the price-observation write are **separate transactions** so a
failure mid-confirm never double-writes — re-running confirms only what isn't yet linked
(`CommittedPriceObservationId` null). Ingestion (`IngestFlyer`) is **idempotent** via the dedup key
(DD5): re-pulling a flyer updates the existing `FlyerImport` and **still-`Pending`** `Deal`s rather
than appending — a `Confirmed`/`Rejected` deal is **frozen** against re-pull (DD13) — and a memory
auto-confirm that already wrote its observation is skipped on re-pull.

---

## 8. Cross-context ports (anticorruption layer)

Deals depends on these interfaces; the owning contexts (or Infrastructure, for the untrusted ones)
implement them. All traffic is by ID (DM-3).

| Port | Direction | Used by | Surface |
|---|---|---|---|
| **IFlyerSource** | **untrusted, external** (Infrastructure, over Flipp) | IngestFlyer, ManageSubscriptions | R: `ListMerchants(postalCode)` → merchant names with active flyers near that postal code (there is **no** store-directory search — Flipp is postal-code-keyed); `PullFlyers(postalCode, merchant)` → per active flyer, `RawDeal[]` + `(flyer_external_id, window, content)`. A merchant may have >1 active flyer. The single fragile seam (D1); the domain sees only `RawDeal`s. |
| **IDealMatcher** | **untrusted AI** (Infrastructure, `ChatClient`) | IngestFlyer | R: `(RawDeal, candidate products) → (suggested_product_id, confidence, reasoning)`. Wraps the household AI key (DM-7, ADR-007) exactly as Intake's matcher; output quarantined, never trusted. |
| **ICatalogProductReader** | read | IngestFlyer, ConfirmDeal, BrowseDeals | R: product search + metadata for matching, review correction, "did you mean", inline create-product (Catalog, DM-10). |
| **ICatalogStoreReader / ICatalogStoreWriter** | read / write | ManageSubscriptions | R: resolve a `catalog.store`; W: ensure a `store` row exists for a subscribed merchant (Catalog-owned reference data, DM-16). |
| **IPriceObservationWriter** | write | ConfirmDeal | W: `RecordObservation(source=deal, …, valid_from, valid_to, store_id, source_ref=deal_id)` — the Pricing seam **designed in DM-17, built in Phase 5** (P5-P adds the window + `store_id` columns and the read models; ADR-010 keeps Pricing the single price owner). Deals' only steady-state downstream write. |
| **IShoppingListWriter** | write | StockUpAlerts | W: `AddItems(product_id, qty, unit, source="deal", source_ref=deal_id)` — the **P2-4 seam, reused** (DM-18/D10). |
| **IPurchaseFrequencyReader** | read | StockUpAlerts | R: frequently-bought products per household (DL-O4) — Inventory `Purchase`-journal rows (lean) or Pricing purchase observations; owning context settled in the app pass. |
| **~~IActiveDealReader~~ — not a Deals port (ADR-010)** | — | — | The "active deal per product" read model lives in **Pricing** ("cheapest active deal", DM-17), **not** Deals: Shopping's badge (D11/§3f) and Recipes/Meal-Planning cost read **Pricing** at read time. Deals' *own* surfaces (Deals page §6a, stock-up alerts) read the `deal` table **in-context**. Deals exposes **no** active-deal port — it is a pure writer into Pricing. |

> **Deals writes *through* Pricing, owning no costing math (D6 / ADR-010).** "Cheapest active deal" and
> "latest price" are **Pricing** read models; Deals merely writes the `source=deal` rows. Phase-5
> reality check: those read models are **designed (DM-17) but were never built** — **P5-P builds them**
> (window + `store_id` on `price_observation` + source-filtered reads), and **P5-9 / P5-9b wire the
> consumers** (Shopping badge, recipe / meal-plan cost) to Pricing. The boundary payoff still holds:
> **no Deals port is needed** — every consumer reads Pricing, keeping the dependency graph a clean star
> around Pricing (ADR-010). What was overstated in earlier drafts was the *tense* ("already built"), not
> the boundary.

---

## 9. Domain events

| Event | Payload | Emitted by |
|---|---|---|
| **FlyerImported** | `householdId, flyerImportId, storeId, pendingCount, at` | `IngestFlyer` on `MarkParsed` (DJ2). Feeds the Home "N deals to review" banner (§0b / [plantry-bpw]). |
| **DealConfirmed** | `householdId, dealId, productId, storeId, validFrom, validTo, by, at` | `ConfirmDeal` (user or auto; `by` null for memory). Hook for stock-up-alert refresh + (deferred) push. |
| **DealRejected** | `householdId, dealId, by, at` | `RejectDeal` (DJ4). |

No Phase-5 cross-context reaction *requires* these (stock-up alerts are a read model recomputed on
demand; Shopping is called directly via a port). Kept light, as Recipes / Meal Planning did — for the
Home banner, audit/attribution, and future consumers (push, analytics).

> **`FlyerImported.pendingCount` is point-in-time.** It is correct **at `MarkParsed`** (every
> newly-staged deal is in-window then). The standing Home banner must **recount against the clock**
> (`Pending` ∧ in-window, DD14), **not** trust the stamped count — otherwise a week-old event keeps
> advertising deals that have since expired.

---

## 10. Invariants (consolidated)

| # | Invariant | Source | Enforced |
|---|---|---|---|
| **DD1** | A `Confirmed` deal has a non-null `ProductId`; a `Rejected` deal has `ProductId = null`; a `Pending` deal has no committed observation | D5 | Aggregate (`Confirm`/`Correct`/`Reject`) |
| **DD2** | Only a `Confirmed` deal projects a `price_observation`, and it records exactly one via `CommittedPriceObservationId`; a **Correct** supersedes with a **new** observation (append-only, never edited) | D6 | `ConfirmDeal` orchestration + Pricing (DM-17/R1) |
| **DD3** | `DealMatchMemory` is unique per `(household_id, store_id, normalized_name)` | D4 | DB `UNIQUE` + upsert |
| **DD4** | `NormalizedName` is **deterministic and reproducible** across pulls **for a given normalizer version** (pure ACL function, never AI-derived); memory rows stamp their `NormalizerVersion` and retain `RawName`, so a normalizer change is a **one-time backfill**, not silent memory decay | DL-O6 | `DealNormalizer` (pure) + version stamp |
| **DD5** | Ingestion is **idempotent / de-duplicated** on `(household_id, store_id, flyer_external_id)` (and content hash); a re-pull updates, never appends duplicate `FlyerImport`/`Deal`s — and a re-pull refreshes only **`Pending`** deals (DD13) | DL-O5 | DB `UNIQUE` + `IngestFlyer` |
| **DD6** | The ACL quarantine — `FlyerImport.raw_flyer` and a `Deal`'s `Suggested*`/`MatchConfidence`/`MatchReasoning` — is **never overwritten** after parse (the provenance half of the ACL) | ADR-007 | App service (write-once) |
| **DD7** | A deal is **Active** iff `Status = Confirmed` **and** `ValidFrom ≤ today ≤ ValidTo` | D9 | Read model (`ActiveDealReader`) |
| **DD8** | Deals **never** writes Inventory — confirming a deal records a price, not a purchase | D8 | No Inventory write port exists in §8 |
| **DD9** | `StoreSubscription` is unique per `(household_id, store_id)` | D7 | DB `UNIQUE` |
| **DD10** | A deal's `ValidFrom ≤ ValidTo`, and the window projects unchanged onto its `price_observation` | D9 | Aggregate + `ConfirmDeal` |
| **DD11** | Confirm/Correct **upserts** `DealMatchMemory`; Reject **may** write a negative memory; both keep memory consistent with the latest human decision | D4, DL-O3 | `ConfirmDeal` / `RejectDeal` |
| **DD12** | `FlyerImport.Status` is monotonic: `Pulling → Parsed` **or** `Pulling → Failed` | D2 | Aggregate |
| **DD13** | A re-pull may **refresh only a `Pending` deal**; a `Confirmed`/`Rejected` deal is **frozen** — ingestion never overwrites its status, resolution, raw fields, or `Price`. (A genuine flyer reprice on an already-resolved item is left as-is in v1; auto-supersede-on-reprice is deferred behind a same-id content-churn telemetry trigger.) Prevents a re-pull silently clobbering a human resolution or invalidating a committed observation's provenance | D5, DL-O3 | `IngestFlyer` (state guard) |
| **DD14** | `Pending` is surfaced in the review queue only **in-window** (`today ≤ ValidTo`); an expired-unreviewed deal is **inert** (not Active, off the queue) but **remains confirmable** as an explicit price-history backfill — and confirmation **always** upserts `DealMatchMemory`, regardless of window | D9 | Read model (`BrowseDeals`) + `Confirm` allows past-window |

---

## 11. Resolved modeling calls

- **DL-O1 — `Deal` is its own aggregate root ✅.** Not a child of `FlyerImport`. A `Deal` has an
  **independent, long-lived lifecycle** — browsed while active (§6a), feeding Pricing across its whole
  validity window, expiring on its own clock, retained as price history (D9) — whereas an
  `ImportLine` is reviewed once and inert. Browse and the Pricing read models query deals **across**
  imports by product and window; coupling the deal to a transient pull envelope (Intake's
  session+line shape) would make those cross-aggregate queries awkward and tie a durable record to an
  ingestion artifact. `Deal` references its source `FlyerImport` by a within-context FK (nullable for
  the deferred manual path). **Upgrade trigger:** none foreseen; if deals ever needed
  per-line-item children (multi-buy tiers) those would be `Deal` children, not a re-root.

- **DL-O2 — `FlyerImport` is a (thin) aggregate ✅.** It earns a root: it is the **async-worker
  unit**, the **raw-payload quarantine** (`raw_flyer` jsonb — the provenance half of the ACL, the
  `import_receipt` analog), the **pull-status** owner (`Pulling`/`Parsed`/`Failed`), and the **dedup
  anchor** (DD5). It owns no typed deal rows (those are separate roots, DL-O1) — pure provenance +
  lifecycle. Folding this onto each `Deal` (the rejected alternative) would duplicate the raw payload
  and the pull status across every deal in a flyer and lose the single retry/dedup unit.

- **DL-O3 — Auto-confirm is immediate but visible & reversible ✅.** A memory-matched deal lands
  `Confirmed` and writes its observation at ingestion (no queue noise for obvious repeats), but is
  flagged `AutoMatched` and stays **Correctable/Rejectable** from the Deals page (DJ3) — a correction
  rewrites memory and supersedes the observation, a reject retracts it (and may write a negative
  memory). This keeps the human authoritative (the C12 principle) without forcing re-review of every
  known item. **Why not queue them as "auto, click to confirm":** that reintroduces the per-flyer
  review toil match memory exists to remove (SPEC §6b "doesn't need review again").

- **DL-O4 — Purchase frequency behind a port ✅.** Stock-up alerts read "buys this often" via
  `IPurchaseFrequencyReader`, hiding the source. **Lean: Inventory's stock journal** (`Purchase`-reason
  rows per product) — the truest record of what the household actually buys; Pricing's purchase
  observations are an equivalent fallback. The port lets the app/schema pass pick without touching the
  Deals model.

- **DL-O5 — Idempotent ingestion ✅.** Dedup on `(household_id, store_id, flyer_external_id)` with a
  `content_hash` (sha256 of the raw payload) as a secondary guard — a byte-identical re-pull is a
  no-op (DD5), and a re-pull of a changed flyer updates the existing `FlyerImport`/`Deal`s rather than
  appending. Mirrors `import_receipt.sha256` duplicate detection. This keeps the price-observation log
  free of duplicate deal rows from repeated worker runs.

- **DL-O6 — `NormalizedName` is a deterministic ACL function ✅.** Lowercase, trim collapse, strip
  pack-size/unit tokens and punctuation — a **pure** `DealNormalizer` (no I/O, no AI), so the
  `DealMatchMemory` key and the dedup behaviour are **stable and reproducible** across pulls (DD4).
  An AI-derived key would drift between runs and silently break match memory. The AI does **matching**
  (name → product), never **keying**. **Version caveat:** determinism holds only *per normalizer
  version* — the stripping rules **will** be tuned against real flyer data, and each change re-keys some
  inputs, silently orphaning memory rows computed by the old rules. So memory stamps `NormalizerVersion`
  and retains `RawName`; a normalizer bump triggers a **one-time backfill** (re-normalize stored raw
  names) rather than letting accumulated memory decay. Read-time multi-version fallback was rejected —
  it spreads version-awareness into the hot lookup path and doesn't compose past two versions.

- **DL-O7 — Confidence reuses Intake's scale ✅.** `MatchConfidence` is `High`/`Low`/`None` verbatim
  (DM-15), so the review-form treatment (`High` pre-filled, `Low` flagged, `None` "Unrecognized"),
  the "show low-confidence only" filter, and the "did you mean" chip pattern all carry over from the
  receipt review the user already knows (§2e). Deals' review queue (§6b) is the Intake review form's
  twin by construction.

---

## 12. Reconciliation notes

- **ADR-010 "Deals — `Store`, `Deal`, `DealMatchMemory`" — amended.** This model refines it:
  (a) the merchant **identity** `Store` is **Catalog-owned** (DM-16), so Deals owns a
  **`StoreSubscription`** config instead (D7); (b) a **`FlyerImport`** provenance/ACL aggregate is
  added for the persisted, async, retryable pull (D2/DL-O2) — ADR-010 folded "flyer ingestion" into
  `Deal`; (c) `Deal` is its **own root** with an active/expired lifecycle (DL-O1), referencing its
  pull envelope by a within-context FK; (d) `DealMatchMemory` is confirmed, extended with an optional
  **negative** memory (DL-O3). The ACL is the **persisted** Intake shape (review-then-commit, raw
  payload quarantined as jsonb, resumable per-deal commit), **not** Meal Planning's transient store.
  **Record as an ADR-010 amendment when the schema lands.**

- **ADR-010 / ADR-007 "Deals ACL specifics (tied to the unresolved Flipp access question)" —
  resolved.** Flipp access is settled to the **unofficial API/scraper behind an `IFlyerSource`
  adapter** (D1); the matcher is an untrusted `IDealMatcher` (`ChatClient`, DM-7) exactly as Intake;
  only user-confirmed / memory-auto-confirmed deals cross into Pricing (D5). The fragile seam is
  isolated in `Plantry.Deals.Infrastructure`.

- **DM-16 closed out.** `catalog.store` lands this phase, so `price_observation.store_id` is populated
  for deal observations and **back-fillable** for historical purchases — the DM-16 deferral resolves.

- **Pricing read models must key historical reasoning off the `validity_window`, not record time.**
  Deals can now feed **backdated** observations — confirming an expired deal as a price-history
  backfill (DD14) writes an observation today with a past window. A naïve "latest price =
  most-recently-*recorded* observation" would let such a backfill masquerade as the current price.
  DD10 projects the window unchanged onto the observation, so Pricing has what it needs — confirm its
  "latest"/"current" reads filter on the **window**, not insertion time. **Flag at the App Services step.**

- **Phase numbering.** Deals is **build-phase 5**: the sequence is P1 Pantry+Intake · P2 Recipes ·
  P3 Meal Planning · **P4 Take Stock (inventory reconciliation)** · **P5 Deals**. Older docs that
  numbered Deals earlier (Phase 3, then Phase 4 before Take Stock was injected at P4) are
  **reconciled in this pass** — recorded in the [ADR-010](../../ADRs/ADR-010.md) amendment 2026-06-22.

---

## Feeds the next step

The **Data Schema** pass rendered this into the `deals` schema —
[`DataModels/deals.md`](../../DataModels/deals.md), **DM-22**:
`store_subscription` (root, `UNIQUE(household_id, store_id)`); `flyer_import` (root, `UNIQUE(household_id,
store_id, flyer_external_id)`, `raw_flyer` jsonb, `status` `text`+`CHECK`, `content_hash` bytea,
`valid_from`/`valid_to`); `deal` (root, `flyer_import_id` within-context FK RESTRICT/nullable, `status`
+ `match_confidence` + `source` `text`+`CHECK`, `normalized_name`, the `suggested_*` ACL columns vs the
resolved `product_id`, `valid_from`/`valid_to`, `committed_price_observation_id` soft-ref); `deal_match_memory`
(root, `UNIQUE(household_id, store_id, normalized_name)`, nullable `product_id` for negative memory,
plus `normalizer_version` and a retained `raw_name` for normalizer-change backfill, DD4) —
UUIDv7 PKs, `household_id` + per-household RLS (ADR-008), per `DataModels/conventions.md`. The
**`catalog.store`** reference table (Catalog-owned, DM-16) was added to [`catalog.md`](../../DataModels/catalog.md)
this phase. `ActiveDeal` and `StockUpAlert` get **no tables** (computed read-side). The ports in §8 become the
application-service interfaces wired in the App Services step (PHASE-5-PLAN, not yet written);
`IFlyerSource` and `IDealMatcher` are implemented in `Plantry.Deals.Infrastructure` (the latter over the
household AI key, DM-7, exactly as Intake wraps its `ChatClient`). **`MealPlanningDbContext`-style
RLS-middleware wiring applies**: the new `DealsDbContext` must be registered in `RlsMiddleware` (the
known gotcha) or reads silently return nothing.
