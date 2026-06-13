# Intake — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 1 (backfilled)
>
> **Purpose:** The shared vocabulary for the Intake bounded context. Key terms — ACL, `raw_parse`, `suggested_confidence`, `Confirm`, `Dismiss`, `Commit` — describe the discipline that separates AI output from domain state.
>
> **Bounded context:** Intake (`intake` schema, Phase 1).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregates & Entities

| Term | Kind | Definition |
|---|---|---|
| **ImportSession** | Aggregate root | One receipt-parse-and-review cycle. Starts `parsing`; becomes `ready` when the pipeline finishes; ends `committed`, `discarded`, or `failed`. Holds the review state for all lines. |
| **ImportLine** | Entity (child of ImportSession) | One receipt line. Dual role: the **editable review row** the user works with, and the **ACL quarantine** for the AI's raw output. |
| **ImportReceipt** | 1:1 child of ImportSession | The immutable source artifact — image bytes or email bytes — plus `raw_text` (OCR/vision output fed to the pipeline). Kept off the hot session row. |

---

## Key Terms

| Term | Definition |
|---|---|
| **ACL** (Anticorruption Layer) | The design pattern Intake implements: the AI's output is quarantined and only user-confirmed data crosses the domain boundary into Catalog, Inventory, and Pricing. |
| **Pipeline** | The two-stage AI parse: Stage 1 (raw text → line items) + Stage 2 (line items → catalog matches). Runs server-side; output lands in `raw_parse`. |
| **`raw_parse`** | The `jsonb` column on `ImportLine` holding the complete AI payload for that line. The quarantine side of the ACL. **Never overwritten** after pipeline completion. |
| **`suggested_confidence`** | `high` / `low` / `none` — the AI's self-assessed match quality. Drives review-form treatment: high-confidence lines are pre-filled; low-confidence lines are flagged for closer inspection. |
| **Stage 1** | Raw receipt text → structured line items (quantity, unit text, price, savings). Output in `raw_parse`. |
| **Stage 2** | Line items → catalog match (suggested product, confidence, reasoning). Output in `raw_parse`. |
| **Review** | The user-facing form where the AI proposal is examined, edited, confirmed, or dismissed. The ACL gate. |
| **User-resolved fields** | The typed columns the user can edit: `product_id`, `sku_id`, `quantity`, `unit_id`, `location_id`, `expiry_date`, `price`. The **only** fields that commit to other contexts. |
| **Confirm** | The user accepts a line as-is or after editing. Status → `confirmed`. The line will commit on Submit. |
| **Dismiss** | The user skips a line (irrelevant, duplicate, etc.). Status → `dismissed`. Retained for audit; never commits. |
| **Submit / Commit** | The user triggers the commit orchestration — confirmed lines are written to Catalog, Inventory, and Pricing, each in its own transaction. Resumable. |
| **Resumable commit** | If commit fails mid-batch, a re-run processes only `confirmed`-but-not-`committed` lines. No double-write. |
| **`merchant_text`** | Free-text merchant name parsed from the receipt by Stage 1. Stored on `ImportSession`; copied to each `price_observation` on commit. The only merchant data in Phase 1. |
| **Email intake address** | The forwarding address in `HouseholdSettings` for the async path — the user forwards a receipt email; Intake processes it in the background. |

---

## Session status lifecycle

```
parsing → ready → committed
                ↘ discarded
       → failed
```

| Status | Meaning |
|---|---|
| `parsing` | Pipeline is running; no user interaction yet |
| `ready` | Pipeline finished; review form is active |
| `committed` | All confirmed lines have committed; session is terminal |
| `discarded` | User abandoned the session without committing |
| `failed` | Pipeline or AI error; `error_detail` is set; surfaced to the user |

---

## Line status lifecycle

```
pending → confirmed → committed
        ↘ dismissed
```

---

## Key Actions

| Verb | Meaning |
|---|---|
| **Scan receipt** | User photographs a receipt; the image is sent to the pipeline; an `ImportSession` is created in `parsing` state. |
| **Forward email** | User forwards a receipt email to the household intake address; async processing creates an `ImportSession`. |
| **Review** | User works the review form — editing, confirming, and dismissing lines. |
| **Confirm** | Accept a line (edited or as-is); status → `confirmed`. |
| **Dismiss** | Skip a line; status → `dismissed`. |
| **Submit** | Trigger commit orchestration for all confirmed lines. |
| **Abandon / Discard** | Session is closed without committing; status → `discarded`. |

---

## Cross-context terms (written by commit, owned elsewhere)

| Term | Owned by | Notes |
|---|---|---|
| `product_id` / new Product | Catalog | Inline-created on commit if no match |
| `stock_journal_entry` | Inventory | The `Purchase` row written per confirmed line |
| `price_observation` | Pricing | The `purchase` observation written per confirmed line |
