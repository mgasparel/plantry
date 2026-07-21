# Purchase-Entry Amendment — Fixing Data-Entry Mistakes Without Breaking the Ledger

**Status:** Accepted design · **Author:** design conversation (mgasparel + Claude) · **Date:** 2026-07-20
**Origin:** `plantry-x3dy` — a receipt line for onions was mis-entered as 1 lb when the receipt said
3 lb; no in-app fix existed, and the correction required raw SQL across four tables in three schemas
(`intake.import_line`, `inventory.stock_journal_entry`, `inventory.stock_entry`,
`pricing.price_observation`), bypassing every invariant. See also `plantry-vmfe` (the same tension
between `Correction`'s blunt semantics and precise attribution, from the consume side).
**Prototype:** `C:\Users\mgasp\OneDrive\Documents\Claude\Projects\Plantry\code\.preview\amend-purchase-entry.html`
(static, links the live `plenish.css`; UX decisions below were locked by eye against it on 2026-07-20).
**Scope:** A sanctioned, audited path to fix the **committed quantity** of a purchase that was entered
wrong at intake review — as an additive compensating ledger entry, never an in-place mutation.
**Out of scope:** price typos (no inventory impact; same machinery extends later), wrong product or
wrong unit (re-commit territory), amending manually-added (non-intake) lots, resurrecting depleted
lots, re-deriving a conversion factor seeded from the mis-entered line (follow-up, see §8).

---

## 1. Motivation

The stock journal is strictly append-only (ADR-011): rows are never updated or deleted, and the only
sanctioned correction is Take Stock with `reason = Correction`. That is the right tool when *physical
reality* diverged from the records (spoilage, forgotten consumption, discovery). It is the wrong tool
when *the record itself* was wrong — an OCR misread or typo on the original Purchase line. Forcing a
data-entry fix through Correction leaves the ledger claiming "1 lb purchased + 2 lb discovered on
recount" when the truth is "3 lb purchased, entered wrong": spend and unit-price analytics are
polluted, and waste/shrinkage analysis inherits a phantom discrepancy.

The fix distinguishes the two situations in the reason taxonomy itself:

- **`Correction`** — recorded stock diverged from reality; the cause is unknown or physical.
- **`Amendment`** *(new)* — the original entry was wrong; the cause is known and it is the entry.

Everything needed for a safe implementation already exists: `ImportLine.MarkCommitted` stores
`JournalId` and `PriceObservationId` (`ImportLine.cs:93-94`), so every committed receipt line knows
exactly which journal row and price observation it produced.

## 2. Decision summary

| # | Decision | Rationale |
|---|----------|-----------|
| A1 | New `StockReason.Amendment`, recorded as a **compensating journal row on the original lot** — `delta = corrected − effective`, bidirectional. Nothing is ever mutated in the journal | Append-only holds verbatim ("a correction is a new row", ADR-011). Analytics reconstruct true purchased quantity as `Σ(Purchase + Amendment)` per lot, attributed to the purchase — no phantom recount |
| A2 | Written by a dedicated aggregate method **`ProductStock.AmendPurchase(entryId, correctedQuantity, …)`** — not through `AddStock` (which creates a new lot) or `Consume` (removal-only, FEFO). The **same `StockEntry`'s** quantity is adjusted by the delta; `StockEntry` gains an `Increase` counterpart to `Deduct` | The physical lot is one lot — its expiry, location, and purchase metadata must not fork. Lots are mutable *current state* by design (Deduct already mutates); only the journal is history. Contrast Take Stock's positive Correction, which deliberately creates a new lot because it genuinely represents newly-discovered stock |
| A3 | **Effective purchased quantity** of a lot = the Purchase row + all prior Amendment rows on that lot (all in the lot's unit). The compensating delta is computed against that. **Repeat amendments are allowed** — each appends another row | Repeats fall out of the model for free; the sheet shows "entered as X · previously fixed to Y" |
| A4 | **Eligibility is ledger-semantic; there is no time window.** (i) The lot originated from intake (`ImportLine.JournalId` linkage exists). (ii) `correctedQuantity > 0` and ≥ the quantity already consumed from the lot (`Σ|negative deltas|` on that `StockEntryId`) — an amendment can never drive the lot negative. (iii) The lot is active (not depleted). (iv) **Closed once any `Correction` row exists for the product dated after the Purchase row** | A typo may be noticed a week later; a clock window is arbitrary. The real backstop is the count: once a Take Stock has reconciled actual-vs-recorded for the product, the discrepancy has already been absorbed and amending afterward double-counts |
| A5 | The Correction-closure check (A4-iv) is **product-level, deliberately conservative** — any Correction row on the product after the purchase, not just rows touching this lot | A Take Stock positive true-up lands as a **new lot** (`AddStock(reason: Correction)`), invisible lot-locally. E.g. onions entered 1 lb with 3 lb on the shelf: a count writes `Correction +2` as a new lot; amending the original to 3 lb afterward would record 5 lb against 3 real. Product-level closure also catches manual correction fix-ups, which carry the same meaning ("someone already reconciled this product") |
| A6 | The Amendment row carries **`SourceType = Intake`, `SourceRef = ImportLine.Id`** | Forward provenance from ledger to receipt line. (The Purchase row's `SourceRef` is null today — linkage runs `ImportLine.JournalId → journal`; the Amendment row does not retrofit that, it just adds its own) |
| A7 | `PriceObservation` is **superseded, never edited**: a new observation is recorded with the corrected quantity, recomputed `UnitPrice`, and the **same `Price`, `ObservedAt`, `SourceRef`**; the new row carries `AmendsId` → old row, and the old row gets **`SupersededById`** bound once (same one-time-bind precedent as `ResolveStore`, DM-16). All pricing reads filter `SupersededById IS NULL`. Repeats chain | The price event's *time* didn't change — only its quantity was wrong, so `ObservedAt` is preserved for price-history windows. `SupersededById IS NULL` keeps the read-side filter O(1) with no subquery; the `AmendsId`/`SupersededById` pair keeps the full audit chain |
| A8 | The observation is **re-derived by re-running the commit-time price derivation** with the corrected quantity — not by naively scaling. If the corrected quantity does not feed the observation, the observation is untouched | Weight-priced lines committed as an each-count (plantry-1mu) record the observation in the *weight* unit; fixing the each-count typo must not touch that observation. Reusing the derivation makes this fall out for free |
| A9 | `ImportLine` gains **`AmendedQuantity` (+ `AmendedAt`)** via a `MarkAmended` method. Intake staging is not a ledger — a field is fine; receipt re-display shows both the entered and amended values | The line is the *source* of the typo; leaving it wrong forces every future reader to reverse-engineer the journal (the exact failure of the raw-SQL incident) |
| A10 | Orchestration: **`AmendCommittedLineCommand` in `Plantry.Intake.Application`** with new ports `IAmendStockPort` / `IAmendPricePort`, mirroring commit's `IAddStockPort` / `IRecordPricePort` (Composition adapters, ADR-014 — no shared transaction). Ordering mirrors commit: amend stock → supersede price → `MarkAmended` → save. A re-run after a mid-sequence failure is safe: a zero-delta stock amend is a no-op, and the price step skips when the live observation already matches the corrected quantity | Same resumability posture as `CommitSessionCommand` (ADR-010): each step is idempotent under retry, so a partial failure is re-driven, not compensated |
| A11 | UI (locked by eye against the prototype): **single entry point — an "Amend" action on the History grid's Purchase row** in Pantry → Product Detail. Canonical `.sheet` with a receipt-provenance strip, one quantity field (unit shown as a fixed suffix, not editable), and a live **"Effect of this fix"** preview in user terms only (on hand and unit price, before → after) — no ledger/implementation language. When amendment is closed (A4-iv), the action opens an **explaining sheet** (why, and what to do instead: recount), not a disabled button. `Amendment` renders as **plain text** in the History reason column like every other reason | The Purchase row is the thing being fixed; one affordance, where the user is already looking at history. The explaining sheet teaches the model at exactly the moment the user hits the boundary |

## 3. Worked example (the 2026-07-20 incident)

Receipt: `ONIONS YELLOW … $3.98`, actually 3 lb; entered 1 lb at review.

| Step | Journal (lot L) | Lot L qty | Price observations |
|------|-----------------|-----------|--------------------|
| Commit | `Purchase +1 lb` (Intake) | 1 | `$3.98 / 1 lb → $3.98/lb` |
| Amend to 3 lb | `Amendment +2 lb` (Intake, SourceRef = line) | 3 | old row superseded; new: `$3.98 / 3 lb → $1.33/lb`, same `ObservedAt` |
| Amend again to 2.5 lb | `Amendment −0.5 lb` | 2.5 | second supersede; `$3.98 / 2.5 lb → $1.59/lb` |

Spend analytics: still one $3.98 purchase. Purchased-quantity analytics: `1 + 2 − 0.5 = 2.5 lb`,
all attributed to the purchase. Waste analytics: untouched — no phantom Correction.

Guard cases:
- 2.5 lb already consumed from the lot → corrected quantity below 2.5 is rejected
  (`Inventory.AmendBelowConsumed`), with the in-sheet validation mirroring the domain error.
- Product counted in a Take Stock after the purchase → amendment closed
  (`Inventory.AmendmentClosedByCorrection`); the sheet explains and suggests a recount.

## 4. Inventory mechanics

- `StockReason.Amendment` joins the taxonomy: bidirectional like `Correction`; permitted **only**
  from `AmendPurchase` — `IsAddition`/`IsRemoval` gates on `AddStock`/`Consume` do *not* admit it.
- `ProductStock.AmendPurchase(StockEntryId, decimal correctedQuantity, Guid importLineId, Guid userId, IClock)`:
  find the lot's Purchase row → effective quantity per A3 → validate A4 → `entry.Increase(delta)` or
  `entry.Deduct(−delta)` → append the Amendment row. Returns the signed delta (for the toast/UI).
- Quantities are in the **lot's unit** throughout — the committed unit is the lot unit, so no
  conversion is involved (unlike `Consume`).

## 5. Pricing mechanics

- Migration: `AmendsId` (nullable, FK self) + `SupersededById` (nullable, FK self) on
  `pricing.price_observation`; partial index on `SupersededById IS NULL` if profiling warrants.
- `PriceObservation.Supersede(replacement)` binds `SupersededById` once (throws if already bound —
  repeats chain off the *live* row, never fork).
- Every pricing read path (`PricingQueries`, `IPriceReader`, deals' `IPriceObservationWriter`
  consumers, `PurchaseStoreBackfill`) filters superseded rows. Audit/history surfaces may opt in to
  the full chain later.

## 6. Intake + Web mechanics

- Reverse lookup for the History grid: given a Purchase journal row, find the committed `ImportLine`
  with that `JournalId` (cross-schema read model, ADR-021). The "Amend" action renders whenever the
  linkage exists; eligibility (A4) is evaluated when the sheet opens — the *closed* state is a
  rendered explanation, not a hidden button.
- The amend sheet posts to a Pantry Detail handler that invokes `AmendCommittedLineCommand`
  (household-scoped via `ITenantContext`, like every command).
- Fragment tests follow the Take Stock / Deals page-test pattern (`Plantry.Tests.Web`).

## 7. Acceptance criteria

1. Amending 1 lb → 3 lb appends exactly one `Amendment +2 lb` row on the original lot, adjusts that
   lot (and product total) to 3 lb, supersedes the price observation ($3.98/lb → $1.33/lb, same
   `ObservedAt`), stamps the line `AmendedQuantity = 3`, and never mutates the Purchase row.
2. A second amendment appends a second compensating row against the new effective quantity; the
   sheet shows "entered as X · previously fixed to Y".
3. Corrected quantity below the consumed-from-lot total is rejected with a domain error; the sheet
   shows the guard message and disables save.
4. After any `Correction` row for the product dated after the purchase (counted-down lot, counted-up
   new lot, or manual fix-up), the amend action opens the explaining sheet and the command rejects.
5. Each-count amendment of a weight-priced line (plantry-1mu) leaves the weight-denominated price
   observation untouched.
6. Pricing queries and deal comparisons never see a superseded observation.
7. A mid-sequence failure re-run completes without double-writing (zero-delta no-op + price-step
   skip).
8. Non-intake lots and depleted lots offer no amend path.

## 8. Follow-ups (explicitly not v1)

- **Price-typo amendment** — same supersede machinery, no inventory leg.
- **Re-derive seeded conversions** — a mis-entered quantity that seeded a weight→each factor
  (`DecideConversionSeed`) leaves a wrong `AiSuggested` conversion behind; amendment should
  eventually re-run the seed decision.
- **Depleted-lot resurrection** — upward amendment of an already-depleted lot.
- **`plantry-vmfe`** — the consume-side attribution twin (deferred-unit-gap voids relabelled as
  Correction); unblocked conceptually by the Amendment/Correction distinction landing in the taxonomy.
