# Context 4 — Intake (`intake` schema) ✅

The **anticorruption layer over the AI** (ADR-007, ADR-010). A receipt — photographed (sync) or forwarded by email (async, SPEC §2a/§2b) — is parsed by the two-stage pipeline (raw text → line items → confidence-scored catalog matches; the `ReceiptPoc` shapes), and the result lands here as a **proposal the user reviews and edits**. Nothing the AI says is trusted: its output is quarantined in `suggested_*` / `raw_parse` columns, and **only user-resolved fields cross the boundary** on commit. Commit is an application-service orchestration that issues commands to *other* aggregates (Catalog create-product, Inventory record-purchase, Pricing record-observation), each its own transaction; the session records what it committed (ADR-010).

`ImportSession` is the aggregate root with `ImportLine` children; the 1:1 `import_receipt` holds the immutable source bytes.

---

**`import_session`** — aggregate root; one per receipt/forwarded email

| Column | Type | Notes |
|---|---|---|
| `import_session_id` | `uuid` PK | + `UNIQUE (household_id, import_session_id)` for child composite FK |
| `household_id` | `uuid` | |
| `source_type` | `text` | `photo` / `email` (CHECK) — the two SPEC §2 entry modes; email path uses `household_settings.email_intake_address` |
| `status` | `text` | `parsing` / `ready` / `committed` / `discarded` / `failed` (CHECK) — `failed` captures a pipeline/AI error so the ACL surfaces it rather than silently dropping |
| `merchant_text` | `text` null | merchant as parsed from the receipt; copied into `price_observation.merchant_text` on commit (DM-16) |
| `error_detail` | `text` null | populated when `status = failed` |
| `parsed_at` | `timestamptz` null | when the pipeline finished (status → `ready`) |
| `committed_at` | `timestamptz` null | |
| `created_by_user_id` | `uuid` | who initiated; soft ref → identity |
| `created_at` / `updated_at` | `timestamptz` | |

---

**`import_receipt`** — 1:1 with session; the immutable source (kept off the hot session row)

| Column | Type | Notes |
|---|---|---|
| `import_session_id` | `uuid` PK | FK → `import_session` (also the PK — 1:1) |
| `household_id` | `uuid` | |
| `content` | `bytea` null | the image / email attachment bytes (ADR-009 — binaries in Postgres); Postgres TOASTs it out-of-line so it never bloats session reads. Object storage is the deferred scale path |
| `content_type` | `text` null | e.g. `image/jpeg`, `message/rfc822` |
| `byte_size` | `int` null | |
| `sha256` | `bytea` null | integrity / duplicate-receipt detection |
| `raw_text` | `text` null | the OCR/vision text fed to pipeline stage 1 — provenance |
| `email_from` | `citext` null | async path only |
| `email_subject` | `text` null | async path only |
| `received_at` | `timestamptz` null | async path: when the forward arrived |
| `created_at` | `timestamptz` | |

---

**`import_line`** — child of `ImportSession`; one per receipt line. The editable review row **and** the ACL quarantine. Typed columns only where they earn it (user-edited/committed fields, plus the fields the UI filters on); the read-only AI proposal lives in `raw_parse`.

| Column | Type | Notes |
|---|---|---|
| `import_line_id` | `uuid` PK | |
| `household_id`, `import_session_id` | `uuid` | composite **FK → `import_session`** (within-context, enforced) |
| `line_no` | `int` | order on the receipt |
| **— AI proposal (read-only) —** | | |
| `receipt_text` | `text` | the line as printed — the row's display anchor; typed because the form leads with it |
| `suggested_confidence` | `text` | `high` / `low` / `none` (CHECK) — drives review-form treatment (SPEC §2e) and "show low-confidence rows" filtering; typed + indexable |
| `raw_parse` | `jsonb` null | the full pipeline payload — stage-1 fields (`quantity`, `unit_text`, `price`, `savings`) and stage-2 fields (`suggested_product_id`, `suggested_product_name`, `reasoning`). Opaque, never overwritten — the provenance half of the ACL |
| **— User-resolved (the only fields that commit) —** | | |
| `product_id` | `uuid` null | soft ref → `catalog.product`; pre-filled from the AI suggestion in `raw_parse` when confidence = `high`, user-overridable |
| `sku_id` | `uuid` null | soft ref → `catalog.product_sku`; price is captured per SKU (SPEC §2e) |
| `quantity` | `numeric(12,3)` null | |
| `unit_id` | `uuid` null | soft ref → `catalog.unit` — the resolved catalog unit (the ACL translation of the free-text parsed unit in `raw_parse`) |
| `location_id` | `uuid` null | soft ref → `catalog.location` |
| `expiry_date` | `date` null | auto-suggested via the expiry chain, user-editable |
| `price` | `numeric(12,2)` null | defaults from the parsed price in `raw_parse`, editable |
| **— Lifecycle & commit linkage —** | | |
| `status` | `text` | `pending` / `confirmed` / `dismissed` / `committed` (CHECK) |
| `created_product_id` | `uuid` null | soft ref — set if commit created a new catalog product (inline create, SPEC §2d) |
| `committed_journal_id` | `uuid` null | soft ref → `inventory.stock_journal_entry` — the purchase row this line produced |
| `committed_price_observation_id` | `uuid` null | soft ref → `pricing.price_observation` |
| `committed_at` | `timestamptz` null | |
| `created_at` / `updated_at` | `timestamptz` | |

---

## Pipeline, ACL, and commit orchestration ✅

**Two-stage parse → proposal.** Stage 1 turns raw receipt text into line items (text, quantity, unit, price, savings); stage 2 scores each against the household's catalog (suggested product + `high`/`low`/`none` confidence + reasoning). The full payload lands in each line's `raw_parse`, with `receipt_text` and `suggested_confidence` lifted out as typed columns; rows start `status = pending` and the session goes `parsing → ready` (or `→ failed`, `error_detail` set).

**Review (the ACL boundary).** The user works the review form (SPEC §2e): high-confidence rows arrive pre-filled (`product_id` from the AI suggestion in `raw_parse`), low-confidence rows are flagged, unmatched rows offer create/link. Edits write only the **user-resolved** columns — `raw_parse` and `suggested_confidence` are never overwritten, preserving "what the AI said" vs "what the user committed." A row the user keeps becomes `confirmed`; a skipped row becomes `dismissed`.

**Commit orchestration.** "Submit all" iterates the `confirmed` lines; per line it issues, each in its own transaction (ADR-010):

1. *(if needed)* Catalog **create-product** / create-SKU → records `created_product_id`;
2. Inventory **record-purchase** writing a `Purchase` journal row with `source_type = Intake`, `source_ref = import_session_id` → records `committed_journal_id`;
3. Pricing **record-observation** (`source = purchase`, `merchant_text` from the session, `source_ref = import_line_id`) → records `committed_price_observation_id`.

The line flips to `committed` only after its writes succeed; because each is a separate transaction, commit is **resumable** — a re-run processes only `confirmed`-but-not-`committed` lines, so a mid-batch failure never double-writes. When no `confirmed` lines remain, the session is `committed`; abandoning it is `discarded`. `dismissed` lines and the raw payload are retained for audit.

---

> **ADR note (reconciled):** ADR-010 (amended 2026-06-06) records that "`ImportSession` … parsed rows (jsonb)" becomes a real `import_line` **child table** (individually edited, matched, dismissed, commit-tracked), with the raw AI payload retained as `raw_parse` jsonb *per row* for ACL provenance.

> **ADR note (ADR-010, reconciled):** merchant identity belongs in **Catalog** (a small `store` reference aggregate), not Deals; Deals (Phase 5) keeps its flyer-source *config* aggregate (`StoreSubscription`) referencing `catalog.store` by ID. **Finalized (DM-16, [pricing.md](pricing.md)):** Phase-1 Intake stays on free-text `merchant_text`, which `price_observation` carries alongside a nullable `store_id` soft-ref; the `store` table is deferred to Phase 5 (lands with Deals, DM-22).
