# Intake — Domain Model

> **Status:** Implemented — Phase 1. Retrospective backfill; the DDD process was formalized after these contexts were designed.
>
> **Purpose:** The anticorruption layer (ACL) over the AI parse pipeline. A receipt — photographed or forwarded by email — is parsed and the result lands here as a *proposal* the user reviews and edits. Nothing the AI says is trusted without user confirmation. Only user-resolved fields cross the boundary on commit, which orchestrates creates/writes to Catalog, Inventory, and Pricing.
>
> **Bounded context:** Intake (`intake` schema, Phase 1). References Catalog by ID for product matching; writes to Catalog, Inventory, and Pricing on commit; reads nothing from Inventory or Pricing directly.
>
> **Code shape:** `ImportSession` is the aggregate root with `ImportLine` children (one per receipt line) and a 1:1 `ImportReceipt` child (immutable source bytes). Commit is a resumable per-line orchestration — each line commits in its own transaction so a mid-batch failure never double-writes.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregate map

| Aggregate root | Identity | Owns (composition) | Lifecycle |
|---|---|---|---|
| **ImportSession** | `ImportSessionId` | `ImportLine[]`, `ImportReceipt` (1:1) | Created on receipt submission; terminal states: `committed` / `discarded` / `failed` |

`ImportLine` is the editable review row and the ACL quarantine for one receipt line. `ImportReceipt` holds the immutable source bytes — kept off the hot session row via a 1:1 table.

---

## Two-stage parse pipeline

```
Photo / Email → ImportReceipt (bytes + raw_text)
  → Stage 1: raw text → line items (quantity, unit_text, price, savings)
  → Stage 2: line items → catalog match (suggested_product_id, confidence, reasoning)
  → lands in ImportLine.raw_parse (jsonb, quarantine)
  → session status: parsing → ready (or → failed)
```

The AI output is entirely quarantined in `raw_parse` and `suggested_confidence` — the ACL boundary. User edits write only to the **user-resolved columns** (`product_id`, `quantity`, `unit_id`, `location_id`, `expiry_date`, `price`). `raw_parse` is never overwritten.

---

## Invariants

| # | Invariant | Enforced |
|---|---|---|
| **R1** | `raw_parse` (AI payload) and `suggested_confidence` are **never overwritten** after pipeline completion — they are the provenance half of the ACL | App service |
| **R2** | Only `confirmed` lines commit to other contexts; `dismissed` and `pending` lines do not cross the boundary | Commit orchestration |
| **R3** | Commit is **resumable**: each confirmed line commits in its own transaction; a re-run processes only `confirmed`-but-not-`committed` lines (no double-write) | Commit orchestration |
| **R4** | A line transitions to `committed` only after all its writes (Catalog, Inventory, Pricing) succeed | Commit orchestration |
| **R5** | `dismissed` lines and the full `raw_parse` payload are **retained** after session commit — never deleted (audit trail) | Architecture |
| **R6** | Session `status` transitions are monotonic: `parsing → ready → committed / discarded`; or `parsing → failed` | App service |

---

## Commit orchestration (per confirmed line)

Each confirmed line issues, in its own transaction:

1. *(if needed)* Catalog **create-product** or **create-SKU** → records `ImportLine.created_product_id`
2. Inventory **record-purchase** → writes a `Purchase` journal row (`source_type = Intake`, `source_ref = import_session_id`) → records `ImportLine.committed_journal_id`
3. Pricing **record-observation** (`source = purchase`, `merchant_text` from session, `source_ref = import_line_id`) → records `ImportLine.committed_price_observation_id`

When no `confirmed` lines remain uncommitted, the session becomes `committed`. Abandoning is `discarded`.

---

## Cross-context ports

| Direction | Context | What's exchanged |
|---|---|---|
| Reads | Catalog | Product search for the review form; `unit` lookup for the ACL translation; existing product metadata to pre-fill the line |
| Writes | Catalog | Inline create-product / create-SKU on commit (when no match exists) |
| Writes | Inventory | `RecordPurchase` — the single Consume-family primitive for intake lots |
| Writes | Pricing | `RecordObservation` with `source = purchase` and `merchant_text` from session |

---

## Key decisions

- **DM-15:** `ImportSession` root + `ImportLine` child table (individually edited, matched, dismissed, commit-tracked). Raw AI payload retained as `raw_parse` jsonb **per row** for ACL provenance. Refines ADR-010's "parsed rows (jsonb)."
- **DM-16 (partial):** `merchant_text` is free text on `ImportSession` (the only merchant data Phase 1 has). It is copied to each `price_observation` on commit. `store_id` is deferred to Phase 3.
- **Resumable commit:** Each line is an independent transaction so a failure mid-batch never double-writes or leaves the session in an inconsistent state.
- **ACL discipline:** The AI is treated as an untrusted external system. Its full output is quarantined; only what the user explicitly confirms crosses the domain boundary.

> Full schema: [../DataModels/intake.md](../DataModels/intake.md)
