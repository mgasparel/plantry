# Receipt Intake History — Browsable Past Intakes + Pantry Provenance Links

**Status:** Accepted design · **Author:** design conversation (mgasparel + Claude) · **Date:** 2026-07-21
**Origin:** `plantry-tmzj` — past receipt imports are invisible after the Done screen: there is no way
to browse what a receipt scan added, and a pantry product's history says only "Intake" with no way to
jump to the receipt that produced the entry.
**Prototype:** `.preview/tmzj-intake-history.html` (static, real plenish.css component styles; UX
decisions below were locked by eye against it on 2026-07-21 — month-grouped grid chosen over card
list, provenance chip treatment approved, Cook→recipe chip added during review).
**Scope:** ① a browsable intake history page, ② a read-only detail view of one committed session,
③ provenance chips on the pantry product History grid (Intake → receipt, Cook → recipe), and the
minimal data-layer work to resolve journal rows to their source.
**Out of scope:** viewing the original receipt image (`ImportReceipt` retention exists; natural
follow-up), amending from the history view (covered by `purchase-entry-amendment.md`), a Cook-event
detail page (the Cook chip links to the *recipe*, not the event), deleting/hiding history rows.

---

## 1. Motivation

Every committed `ImportSession` is already retained forever with its full line set — merchant, header
metadata (purchase date/time, subtotal/tax/total, receipt number, payment descriptor), per-line
receipt text, the user-resolved product/quantity/price, dismissed lines, and the commit linkage
`JournalId` / `PriceObservationId` / `CreatedProductId` (`ImportLine.cs:93-95`). The Upload page's
"Recent intakes" panel (`GetRecentSessionsQuery`) already reads this data. What's missing is purely
read-side: a page to browse it, a page to view one session, and the journal→source resolution that
lets a pantry history row point back at its receipt.

The provenance asymmetry today: Cook journal rows already carry `SourceRef = cookEventId`
(`InventoryProducerAdapter.cs:75`, `InventoryConsumerAdapter.cs`), and Amendment rows carry
`SourceRef = ImportLine.Id` (purchase-entry-amendment §A6) — but the Purchase rows written by intake
commit carry `SourceRef = null`, because `AddStockAdapter` never passes one (`AddStockAdapter.cs:26-29`
— the `AddStockCommand` parameter exists and is simply unused). Intake is the only source type not
honouring the DM-14 "what triggered this" slot.

## 2. Decision summary

| # | Decision | Rationale |
|---|----------|-----------|
| H1 | **Forward fix:** `AddStockAdapter` passes `sourceRef = importLineId` on intake commit. `IAddStockPort.AddStockAsync` gains the parameter; `CommitSessionCommand` supplies the line id | Brings intake in line with Cook (event-level ref) and Amendment rows (§A6 uses `ImportLine.Id` — same grain chosen here for consistency). One hop resolves both the session (`line.SessionId`) and the deep-link anchor (the line itself) |
| H2 | **Historical rows** (`SourceType = Intake`, `SourceRef IS NULL`) resolve at query time via the reverse lookup `import_lines.journal_id = journal.Id`. **No backfill migration.** New index on `intake.import_line (household_id, journal_id)` | `journal_id` is written for every committed line since the initial schema, so coverage is complete. A cross-schema backfill (Inventory reading Intake) buys nothing the indexed lookup doesn't; the ledger stays untouched (ADR-011) |
| H3 | `StockJournalRow` gains `JournalId`, `SourceRef` (raw Guid). Chip resolution happens in the **page model via a Composition-side provenance reader**, not inside `InventoryQueries` | Inventory must not read Intake or Recipes (Gate 2 — IDs only). Same pattern as `ShoppingRecipeReaderAdapter`: the composition root joins contexts; the query service stays context-pure |
| H4 | New port `IStockProvenanceReader` (Web-defined, Composition-implemented) with one batch call: journal rows in → `{journalId → ProvenanceChip}` out. Intake rows: resolve line (by `SourceRef`, else by `journal_id`) → session → `Receipt chip {store, date, sessionId, lineId}`. Cook rows: `SourceRef = cookEventId` → recipe → `Recipe chip {name, recipeId}` | One batched round-trip per page render; unresolvable rows (deleted recipe, foreign ref) degrade to the plain source text — the chip is progressive enhancement, never a 404 |
| H5 | **History page at `/Intake/History`** — month-grouped data-grid (store · date · status badge · items · total), month blocks paged by "Show earlier" (htmx append). Committed store names link to the session detail; `Ready` rows get a **Resume** button (→ Review); `Failed`/`Discarded` rows render muted, unlinked | Locked by eye: the grid matches the Pantry/Catalog table family and scans totals/status better than the card-list alternative. Failed/discarded stay visible — the list is a truthful log of every scan |
| H6 | History row projection: item count = committed-line count (parsed-line count for `Ready`); total = `session.Total` when parsed, else Σ committed `line.Price`, else Σ `SuggestedPrice` (Ready), else "—" | `GetRecentSessionsQuery` sums `SuggestedPrice` today, which is wrong for committed sessions (user may have corrected prices). Committed rows prefer receipt truth, then user-resolved truth |
| H7 | **Session detail at `/Intake/Session?id={sessionId}`** — read-only, **`Committed` sessions only**: `Ready` redirects to Review, `Failed`/`Discarded`/`Parsing` → History (they have no detail to show; NotFound for foreign/unknown ids per tenancy) | One page, one state. Review remains the sole surface for a live session |
| H8 | Detail layout (locked by eye): page-header (eyebrow "Receipt intake", title = store, subtitle = purchase date · commit recency · items added), a **receipt-stats strip** (items added / stocked value / receipt total / tax) + meta line (receipt #, payment, scanned-by/when), then the **line grid in receipt order**: raw receipt text (mono) · "Added to pantry as" product link · qty · price. Badges: `≈ N each` on weight→each lines (`HasEachEstimate`), `New product` on `CreatedProductId` lines. **Dismissed lines stay in place**, struck through, "Dismissed during review" | The receipt is a factual record — hiding dismissed lines would misrepresent it. Receipt order (LineNo) preserves the paper artifact's shape |
| H9 | Each line row carries `id="line-{ImportLineId}"`; arriving with that fragment highlights the row (`.line--target`: accent-subtle background + 3px inset accent bar) | The deep-link landing state — without it a link into a 24-line receipt leaves the user hunting |
| H10 | Product links on detail lines go to **pantry product detail**, falling back to catalog product detail when the product holds no stock record (mirrors plantry-kkeg's xlink fallback semantics); a product deleted since commit renders as plain text | Never a dead link; the pantry view is where the stock landed, so it's the primary target |
| H11 | **Pantry History grid Source column** renders provenance chips (`.src-chip`): Intake → `🧾 {store} · {d MMM} →` linking `/Intake/Session?id={sessionId}#line-{lineId}`; Cook → `{recipe} →` with a chef's-hat icon, linking the recipe detail. Manual rows and unresolvable sourced rows keep today's plain muted text | Locked by eye. Only rows with a resolvable destination earn an affordance; store may read "Unknown store" |
| H12 | **Entry points:** Upload's Recent-intakes panel header gains "View all →" (→ History); committed rows in that panel become links to their session detail. Panel otherwise unchanged; no new nav item | History is one step from where receipts are born; the panel already *is* the miniature history |
| H13 | New CSS in `plenish.css`: `.src-chip`, `.receipt-stats` (+ meta line), `.intake-month`, `.line--target` / `.line--dismissed` row states. `.src-chip` is added to the Dev component library (cross-cutting: pantry history now, plausibly Shopping contribution rows later). The rest are page-scoped | Per the UI-work rules: the chip is a genuinely reusable primitive; the stats strip and month separator are feature compositions of existing tokens |

## 3. Read-side queries (Intake.Application)

- **`GetIntakeHistoryQuery`** — replaces/extends `GetRecentSessionsQuery`'s projection per H6, paged
  (month-block granularity; repository gains a paged/before-cursor variant of `ListRecentAsync`).
  Row: `(SessionId, Store, Date, Status, ItemCount, Total)`. Date = `PurchaseDate` when parsed, else
  `CreatedAt` (a receipt scanned days after shopping should sort/display by purchase day when known).
- **`GetCommittedSessionDetailQuery`** — session by id, `Committed` guard (H7), lines ordered by
  `LineNo`, projected with the per-line badges' inputs (`HasEachEstimate`, `CreatedProductId`,
  dismissed status). Product display names resolve through the existing catalog read facade at the
  page boundary (line stores only ids).
- `GetRecentSessionsQuery` adopts the H6 amount rule (the Upload panel currently shows a
  suggested-price sum even for committed sessions).

## 4. Provenance resolution (Composition)

`IStockProvenanceReader.ResolveAsync(IReadOnlyList<(Guid JournalId, StockSourceType Source, Guid? SourceRef)>)`
→ `IReadOnlyDictionary<Guid, ProvenanceChip>` where
`ProvenanceChip = (string Label, string? Href)`-shaped view data assembled per H4:

- **Intake:** batch — lines by `SourceRef` (line ids, H1-era rows) ∪ lines by `journal_id` (legacy
  rows, H2 index) → sessions → chip. Session `MerchantText` null → "Unknown store".
- **Cook:** batch — cook events by `SourceRef` → recipes → chip. Deleted recipe → no chip.
- Household scoping applies throughout (all lookups tenant-filtered, mirroring RLS).

## 5. Tests

- Adapter: intake commit writes `SourceRef = importLineId` (H1); existing idempotency behaviour
  unaffected.
- Provenance reader: new-style row (SourceRef), legacy row (journal_id fallback), Cook row,
  deleted-recipe and foreign-household rows degrade chip-less.
- Page tests: History grouping/paging/status rendering (H5–H6); Session detail state guard (H7),
  line order, badges, dismissed rendering, anchor ids (H8–H9); Pantry detail chip rendering and
  plain-text fallback (H11); Upload panel links (H12).

## 6. Follow-ups (not this issue)

- "View original receipt" on the session detail (`ImportReceipt` image retention already exists).
- Amend action surfaced on the session detail's lines (today it lives on the pantry History grid —
  `purchase-entry-amendment.md` A11; cross-linking the two surfaces may be worth it once both ship).
- Shopping contribution rows adopting `.src-chip` for their Recipe/MealPlan/Deal sources.
