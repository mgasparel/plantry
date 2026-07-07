# Deals (`deals` schema) — Phase 5 ✅

> Renders [`Domains/Deals/deals-domain-model.md`](../Domains/Deals/deals-domain-model.md) into tables. Authority for *rationale* is the domain model + ADRs; this file holds the *shape*. Deals is a **core** context that wraps an untrusted, fragile external flyer feed (Flipp) behind an anticorruption layer (ADR-007/ADR-010) and turns confirmed flyer items into `deal` price observations. It references Catalog, Pricing, Shopping, Inventory, and Identity **by ID only** (DM-3), with hard FKs only **within** the `deals` schema. Its single steady-state downstream **write** is one `pricing.price_observation` per confirmed deal (the Pricing seam designed in DM-17, built in P5-P/D6); its only other mutable cross-context write is a stock-up-alert "add to shopping list" (DM-18/D10).

Four flat aggregates, none with child entities (domain model §2): **`StoreSubscription`** (which merchants the household pulls flyers from), **`FlyerImport`** (one pull of one store's flyer — the ACL provenance envelope, `raw_flyer` quarantined as jsonb), **`Deal`** (one normalized, reviewable deal — its **own** long-lived root, not a child of the import), and **`DealMatchMemory`** (the remembered `(store, normalized_name) → product` resolution that skips re-review). `ActiveDeal` and `StockUpAlert` are **computed read-side — never tables** (domain model §7). The merchant **identity** is `catalog.store`, Catalog-owned reference data whose table **lands this phase** ([catalog.md](catalog.md), DM-16); Deals references it by `store_id`.

This is deliberately the **persisted** ACL — the Intake `import_session` shape (DM-15), **not** Meal Planning's transient pending store (DM-21) — because flyer review is **async and spread over time** (the queue waits for the user, SPEC §0b), not done in one sitting (D2).

---

**`store_subscription`** — aggregate root; the household's standing choice to pull a store's flyers (§7e / DJ1)

| Column | Type | Notes |
|---|---|---|
| `store_subscription_id` | `uuid` PK | UUIDv7 |
| `household_id` | `uuid` | tenancy (RLS) |
| `store_id` | `uuid` | soft ref → `catalog.store` (DM-16); `UNIQUE (household_id, store_id)` — **one subscription per merchant** (DD9) |
| `postal_code` | `text` | the location the flyer is pulled for — Flipp's feed is postal-code-keyed (`/data?postal_code=…`, filtered by merchant name; **no stable store-directory lookup**). Captured on the Deals subscription page (§7e), **not** a household-global setting. The ingest worker reads it off the subscription |
| `is_active` | `boolean` NOT NULL DEFAULT `true` | paused/unsubscribed subscriptions are **skipped** by the ingest worker but **retained** with their match memory (D9); unsubscribe is a soft-deactivate, never a delete |
| `last_pulled_at` | `timestamptz` null | worker / UI "last updated" bookkeeping |
| `last_flyer_external_id` | `text` null | the last pulled flyer's external id — the dedup anchor (DD5/DL-O5) |
| `created_at` / `updated_at` | `timestamptz` | |

The async ingest worker iterates `is_active = true` subscriptions (DJ2), grouping by `postal_code` (one Flipp `/data` call per distinct postal code, then fan out to the subscribed merchants — matched by case-insensitive merchant-name substring, per the Flipp POC `flipp_client.py`). Subscribing first **ensures** the `catalog.store` identity row exists (via Catalog), then inserts the subscription referencing it by ID (D7) with the chosen `postal_code` — re-subscribing to a previously-removed store reuses the existing `store` row and its `deal_match_memory`, so previously-learned matches still skip review.

---

**`flyer_import`** — aggregate root; one pull of one store's flyer. The ACL provenance envelope, the async-worker unit, and the de-dup anchor (D2/DL-O2). **Retained** — never deleted (audit, like `intake.import_receipt`)

| Column | Type | Notes |
|---|---|---|
| `flyer_import_id` | `uuid` PK | + `UNIQUE (household_id, flyer_import_id)` for the `deal` within-context composite FK |
| `household_id` | `uuid` | tenancy (RLS) |
| `store_id` | `uuid` | soft ref → `catalog.store`; which store's flyer |
| `flyer_external_id` | `text` | Flipp's flyer id; `UNIQUE (household_id, store_id, flyer_external_id) WHERE status = 'parsed'` — the **dedup key** (DD5): a re-pull updates the Parsed row, never appends a duplicate Parsed envelope. Only Parsed rows occupy the key; **Failed attempts are retained as separate audit rows** (a materialize fault no longer poison-pills the flyer — it retries next cycle, plantry-0l05) |
| `content_hash` | `bytea` null | sha256 of the raw payload — secondary dedup (a byte-identical re-pull is a no-op, DL-O5; mirrors `import_receipt.sha256`) |
| `valid_from` | `date` | the flyer's run-date start; copied onto each `deal` (D9) |
| `valid_to` | `date` | the flyer's run-date end; copied onto each `deal` |
| `raw_flyer` | `jsonb` | the **full raw pull payload** — the ACL quarantine; **set once at `Start`, never overwritten** after parse (DD6). Opaque to the domain |
| `status` | `text` | `pulling` / `parsed` / `failed` (CHECK) — the async-ingestion lifecycle; monotonic `pulling → parsed` **or** `pulling → failed` (DD12) |
| `error_detail` | `text` null | populated when `status = failed` (Flipp unreachable / parse error, DJ2 edge) — the fragile-source risk surfaces here, contained |
| `pulled_at` | `timestamptz` | when the pull ran |
| `parsed_at` | `timestamptz` null | when normalization + match finished (`status → parsed`) |
| `created_at` / `updated_at` | `timestamptz` | |

`flyer_import` holds **no** typed deal rows — those are separate `deal` roots. It is pure provenance + pull lifecycle (the deliberate split from Intake, where `import_line`s are children of the session — DL-O1/O2).

---

**`deal`** — aggregate root; one normalized, reviewable deal. Its **own** root (browsed while active, feeding Pricing across its window, expiring on its own clock — not a child of `flyer_import`, DL-O1). **Retained** as price history (D9). The editable review row **and** the ACL quarantine: typed columns where they earn it (matched/committed fields + what the UI filters on); the read-only match proposal sits beside them, never overwritten

| Column | Type | Notes |
|---|---|---|
| `deal_id` | `uuid` PK | UUIDv7 |
| `household_id` | `uuid` | tenancy (RLS) |
| `flyer_import_id` | `uuid` null | composite **FK → `flyer_import (household_id, flyer_import_id)`**, `ON DELETE RESTRICT` (within-context, enforced) — the one real cross-aggregate FK in this schema (DM-3 permits hard FKs within a context). **Null** only for the deferred manual-entry path (D12) |
| `store_id` | `uuid` | soft ref → `catalog.store`; denormalized from the import for query / read models |
| `source` | `text` NOT NULL | `CHECK (source IN ('flyer','manual'))` — discriminator; only `flyer` is built in v1, `manual` left in the model (D12) |
| **— Raw flyer fields (ACL, read-only after parse) —** | | |
| `raw_name` | `text` | the item as advertised — the review row's display anchor; typed because the form leads with it |
| `brand` | `text` null | |
| `size` | `text` null | the advertised pack/size text |
| `price` | `numeric(12,2)` | the advertised sale price for `quantity` |
| `quantity` | `numeric(12,3)` null | pack size the price is for — feeds `unit_price` normalization in Pricing |
| `unit_id` | `uuid` null | soft ref → `catalog.unit`; the unit of `quantity` (resolved in the ACL where possible) |
| `sale_story` | `text` null | "2 for $5" / "Save $1.50" — free-text provenance shown in review (N9) |
| `normalized_name` | `text` | the deterministic key (DD4/DL-O6); with `store_id`, the `deal_match_memory` lookup key |
| **— Match proposal (ACL quarantine, never overwritten — DD6) —** | | |
| `suggested_product_id` | `uuid` null | soft ref → `catalog.product`; the matcher's pick (memory or AI) — read-only provenance |
| `match_confidence` | `text` | `high` / `low` / `none` (CHECK) — reuses Intake's scale (DM-15, DL-O7); drives review-form treatment + the "show low-confidence only" filter; typed + indexable. **Never** auto-confirms (only memory does, D5) |
| `match_reasoning` | `text` null | the AI's rationale (or "remembered match") — provenance |
| **— User-resolved (the only field that commits) —** | | |
| `product_id` | `uuid` null | soft ref → `catalog.product`; the resolved match, set on Confirm/Correct (auto-confirm copies `suggested_product_id`). **Null** while `pending` or after `rejected` |
| **— Lifecycle & commit linkage —** | | |
| `status` | `text` NOT NULL | `pending` / `confirmed` / `rejected` (CHECK) — DD1 |
| `valid_from` | `date` | from the flyer (`flyer_import` window, copied per D9); `CHECK (valid_from <= valid_to)` (DD10) |
| `valid_to` | `date` | **Active** = `status = confirmed` ∧ `valid_from <= today <= valid_to` (DD7, a read-model predicate) |
| `auto_matched` | `boolean` NOT NULL DEFAULT `false` | true if `deal_match_memory` auto-confirmed it — drives the "auto-matched" marker the user can still Correct/Reject (DL-O3) |
| `committed_price_observation_id` | `uuid` null | soft ref → `pricing.price_observation`; the row this deal projected on confirm (DD2). A **Correct** supersedes with a *new* observation (append-only, never edited) |
| `reviewed_by_user_id` | `uuid` null | soft ref → identity; who confirmed/corrected/rejected — **null** for memory auto-confirm |
| `reviewed_at` | `timestamptz` null | |
| `created_at` / `updated_at` | `timestamptz` | |

`RawDeal` — the `IFlyerSource` adapter's stage-1 output per flyer item (`{ raw_name, brand, size, price, quantity, unit_id?, sale_story, window }`) — is a **transient value object, not a table**: it is normalized and materialized into a `deal`, and its raw form survives only inside `flyer_import.raw_flyer`. The deal-side twin of an Intake stage-1 line item.

---

**`deal_match_memory`** — aggregate root; the remembered resolution that skips re-review (D4 / SPEC §6b). One per `(household, store, normalized_name)`

| Column | Type | Notes |
|---|---|---|
| `deal_match_memory_id` | `uuid` PK | UUIDv7 |
| `household_id` | `uuid` | tenancy (RLS) |
| `store_id` | `uuid` | soft ref → `catalog.store` — **store-scoped by design** (DD3, see below) |
| `normalized_name` | `text` | with `store_id`, `UNIQUE (household_id, store_id, normalized_name)` (DD3) — the auto-confirm key on the next pull |
| `raw_name` | `text` | the raw advertised name this key was derived from — **retained** so a normalizer change can re-derive the key without waiting for the item to reappear (DD4) |
| `normalizer_version` | `int` NOT NULL | the `DealNormalizer` version that produced `normalized_name`; a bump flags rows for a **one-time backfill** rather than silent memory decay (DD4) |
| `product_id` | `uuid` null | soft ref → `catalog.product`; the remembered product. **Null = negative memory** ("not a tracked product", DJ4 step 4) |
| `last_confirmed_by_user_id` | `uuid` null | soft ref → identity; provenance |
| `created_at` / `updated_at` | `timestamptz` | |

**Store-scoped key (DD3).** The key includes `store_id` rather than being household-global because the same `normalized_name` can resolve to **different products across stores** for brand-less generics ("2% milk", "bananas"). A household-global key would risk a **silent cross-store mis-auto-confirm** — memory auto-confirms the wrong product, and `AutoConfirm` writes a wrong `price_observation` — exactly the silent auto-error the human-authoritative posture (DL-O3) exists to prevent. The store-scoped key's only cost is re-review per store for identically-named generics — visible, bounded, self-correcting. If that friction ever proves real, the fix is a UI "apply to all my stores?" affordance (one memory row per subscription), **not** a key change. Negative memory is store-scoped on the same grounds.

---

## Read models (computed, never tables)

Per domain model §7 — computed fresh at query time, **no storage**:

| Read model | Source |
|---|---|
| **ActiveDeal** | a `deal` that is `status = confirmed` **and** in-window (`valid_from <= today <= valid_to`, DD7), projected per product — powers the **Deals page** (§6a) and stock-up alerts, **in-context**. **Not exposed to Shopping** (ADR-010): the deal badge (D11/§3f) and deal-aware cost read **Pricing's** cheapest-active-deal read model, not Deals. Read-side over `deal` + clock; nothing stored |
| **StockUpAlert** | `StockUpAlerts.Compute()`: frequently-bought products (`IPurchaseFrequencyReader`, DL-O4 — Inventory `Purchase`-journal rows lean, or Pricing purchase observations) ∩ **ActiveDeal** (D10/§6c). Carries the product, the cheapest active deal's store + price, and the validity window; recomputed on demand, **never stored** |
| **Pending review queue** | `BrowseDeals`: `status = pending` ∧ in-window (`today <= valid_to`, DD14) — an expired-unreviewed deal **drops off the queue** but stays confirmable as an explicit price-history backfill. Product names via `ICatalogProductReader` |

> **`FlyerImported.pendingCount` is point-in-time** — correct at `MarkParsed`, but the standing Home "N deals to review" banner (§0b) must **recount against the clock** (`pending` ∧ in-window, DD14), not trust the stamped count, or a week-old event keeps advertising expired deals.

---

## Cross-context references (by ID, no enforced FK — DM-3)

| Column | Points at (soft ref) |
|---|---|
| `store_subscription.store_id`, `flyer_import.store_id`, `deal.store_id`, `deal_match_memory.store_id` | `catalog.store` (DM-16) |
| `deal.product_id`, `deal.suggested_product_id`, `deal_match_memory.product_id` | `catalog.product` (DM-10) |
| `deal.unit_id` | `catalog.unit` |
| `deal.committed_price_observation_id` | `pricing.price_observation` (DM-17) |
| `deal.reviewed_by_user_id`, `deal_match_memory.last_confirmed_by_user_id` | identity user |
| `*.household_id` | identity household |

The only **enforced** FK that crosses an *aggregate* boundary is `deal.flyer_import_id` — and it stays **within** the `deals` schema (DM-3 permits within-context FKs), `RESTRICT` because `flyer_import` is retained, never deleted. Confirming a deal writes through `pricing.RecordObservation` (DM-17 — the seam designed there, built in P5-P); a stock-up alert writes through `shopping.AddItems` (DM-18, the P2-4 seam) — never a direct table write (ADR-010). These soft-refs and the untrusted feed/matcher become the §8 application-service ports in the App Services step.

---

## Resolved calls ✅

1. **Four flat aggregates, none a child of another (domain model DL-O1/O2).** A `deal` is its **own** root — browsed while active, feeding Pricing across its whole window, expiring on its own clock, retained as price history — whereas an `import_line` is reviewed once and inert (DM-15). Browse and the Pricing read models query deals **across** imports by product and window, so coupling the deal to a transient pull envelope (Intake's session+line shape) would make those cross-aggregate queries awkward. `flyer_import` earns its own root as the **async-worker unit**, the **raw-payload quarantine**, the **pull-status** owner, and the **dedup anchor** — but owns no typed deal rows. `deal` references it by a within-context composite FK (nullable for the deferred manual path, D12).

2. **Persisted ACL — the Intake shape, not Meal Planning's (D2).** The raw pull (`flyer_import.raw_flyer` jsonb) and the proposed matches (`deal.suggested_*`/`match_confidence`/`match_reasoning`) **survive sessions** because flyer review is async and spread over time (§0b). This mirrors `intake.import_session`/`import_receipt` (DM-15) and is the deliberate **opposite** of Meal Planning's transient, session-keyed pending store (DM-21). The ACL quarantine columns are **write-once** — never overwritten after parse (DD6), the provenance half of the boundary; only the user-resolved `product_id`/`status` commit.

3. **Idempotent, de-duplicated ingestion (DD5/DD13).** `UNIQUE (household_id, store_id, flyer_external_id)` on `flyer_import` plus a `content_hash` secondary guard make a re-pull **update** the existing import rather than append a duplicate (mirrors `import_receipt.sha256`). On a re-pull, ingestion refreshes **only still-`pending`** deals; a `confirmed`/`rejected` deal is **frozen** — its status, resolution, raw fields, and `price` are never overwritten (DD13), so a re-pull can never silently clobber a human resolution or invalidate a committed observation's provenance. (A genuine flyer reprice on an already-resolved item is left as-is in v1; auto-supersede-on-reprice is deferred behind a same-id content-churn telemetry trigger.)

4. **`normalized_name` is a deterministic ACL function, versioned (DD4/DL-O6).** Lowercase, trim, strip pack-size/unit tokens and punctuation — a **pure** `DealNormalizer` (no I/O, no AI), so the memory key and dedup behaviour are stable and reproducible across pulls. Determinism holds only **per normalizer version** — the stripping rules *will* be tuned against real flyer data, and each change re-keys some inputs — so `deal_match_memory` stamps `normalizer_version` and retains `raw_name`: a normalizer bump triggers a **one-time backfill** (re-normalize stored raw names), not accumulated memory decay. The AI does **matching** (name → product), never **keying**.

5. **Confirm projects exactly one observation; memory is the only auto-confirm (DD1/DD2/DD11).** Only a `confirmed` deal writes a `pricing.price_observation` (`source=deal`, the validity window, `store_id`, `source_ref=deal_id`) and records it via `committed_price_observation_id`. A **Correct** on an already-confirmed auto-match supersedes with a **new** observation (Pricing append-only, never edited — DM-17/R1) and Repoints memory; a **Reject** writes no observation and may write a negative memory. `match_confidence` shapes review **treatment** only; **only `deal_match_memory`** (a prior human decision) auto-confirms (D4/D5). The state-flip, the memory upsert, and the observation write are **separate transactions** (Intake's resumable-commit discipline) so a mid-confirm failure never double-writes — re-running confirms only what isn't yet linked (`committed_price_observation_id` null).

6. **Single-row invariants are DB constraints; the rest live in app services.** `CHECK`s express the single-row rules: `status`/`match_confidence`/`source` enums, `valid_from <= valid_to` (DD10), the `(household_id, store_id, flyer_external_id)` / `(household_id, store_id, normalized_name)` / `(household_id, store_id)` uniques (DD5/DD3/DD9), and `flyer_import.status` monotonicity is guarded in the aggregate. The cross-aggregate orchestration rules — confirm writes exactly one observation (DD2), re-pull freezes resolved deals (DD13), the in-window queue predicate (DD14) — span rows or reference the clock and are enforced in `IngestFlyer` / `ConfirmDeal` / `BrowseDeals`, exactly as Intake enforces its commit orchestration and Meal Planning its cross-row rules.

---

> **RLS.** Every table carries `household_id` and a per-household row-level-security policy (ADR-008 / DM-1). Tenant-safe child FKs use the `(household_id, parent_id)` composite pattern (conventions.md) — here `deal` → `flyer_import (household_id, flyer_import_id)`. **The new `DealsDbContext` must be registered in `RlsMiddleware.InvokeAsync`** — omitting it leaves `_householdId` empty so the query filter returns nothing (the Recipes P2-1 / Meal Planning P3 gotcha).

> **`catalog.store` lands this phase.** The merchant-identity reference table (Catalog-owned, DM-16) is added in [catalog.md](catalog.md) with Deals — it is the merchant management UI's home (§7e) and the target of every `store_id` soft-ref here. Its arrival closes the DM-16 deferral: `pricing.price_observation.store_id` is now populated for deal observations and **back-fillable** for historical purchase observations.

> **ADR-010 reconciliation.** This schema, with the domain model, **amends ADR-010's "Deals — `Store`, `Deal`, `DealMatchMemory`"** (see [ADR-010](../../ADRs/ADR-010.md) amendment, and domain model §12): (a) the merchant **identity** `Store` is **Catalog-owned** (DM-16), so Deals owns a **`StoreSubscription`** config instead; (b) a **`FlyerImport`** provenance/ACL aggregate is added for the persisted, async, retryable pull (D2/DL-O2) — ADR-010 folded ingestion into `Deal`; (c) `Deal` is its **own root** with an active/expired lifecycle (DL-O1); (d) `DealMatchMemory` is confirmed, extended with optional **negative** memory (DL-O3). The ACL is the **persisted** Intake shape (review-then-commit, raw payload quarantined as jsonb), **not** Meal Planning's transient store. The "Deals ACL specifics (tied to the Flipp access question)" deferred in ADR-010 §Deliberately-deferred is **resolved**: Flipp via an unofficial API/scraper behind an `IFlyerSource` adapter (D1).

> **Feeds the next step.** The domain model §8 ports — `IFlyerSource`, `IDealMatcher`, `ICatalogProductReader`, `ICatalogStoreReader`/`Writer`, `IPriceObservationWriter`, `IShoppingListWriter`, `IPurchaseFrequencyReader` — become the application-service interfaces wired in the **App Services** pass. Per ADR-010 the Shopping deal badge and deal-aware cost read **Pricing's** cheapest-active-deal read model, so Deals exposes **no** active-deal port. `IShoppingListWriter` (Shopping P2-4) already exists and is reused; `IPriceObservationWriter` (Pricing) is reused but **extended in P5-P** (deal window + `store_id` + read models, per DM-17); `IFlyerSource` (the fragile feed) and `IDealMatcher` (the untrusted matcher) are new, implemented in `Plantry.Deals.Infrastructure` — the latter over the household AI key (DM-7, ADR-007) exactly as Intake wraps its `ChatClient`.
