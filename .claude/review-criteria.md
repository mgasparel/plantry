# Plantry Review Criteria

Canonical gate definitions for all Plantry code review. Used by `/plantry-code-review`
and the autonomous pipeline's Opus critic sub-agent. **Changes must be made here —
do not duplicate or diverge in either consumer.**

---

## Gate 1 — Standard correctness, security, tests, reuse

- Correctness: logic errors, edge cases, off-by-ones, null/empty handling, async/await
  misuse, race conditions, transaction boundaries.
- Security: injection (incl. raw SQL/EF interpolated strings), XSS in Razor output,
  authz checks on every handler, secrets in code/config.
- Tests: do behavior changes have corresponding coverage? Are obvious mutation gaps
  (per `stryker-config.json`) plausible for the change?
- Reuse/simplification: duplicated logic, unnecessary abstraction, dead code.

## Gate 2 — Bounded-context and aggregate discipline

- **No cross-context table reads.** A context's repository/EF queries touch only its
  own schema (`identity`, `catalog`, `inventory`, `intake`, `pricing`, `recipes`,
  `meal_planning`, `shopping`, `deals`). If `Plantry.Recipes` needs Inventory data, it
  calls Inventory's application service or reads its read model — it never queries
  `inventory.*` tables directly.
- **Cross-context references are IDs only, never embedded entities.** A
  `Recipe.Ingredient` holds a `ProductId`, not a `Product`. Catalog is the universal
  upstream supplier — nothing downstream mutates a `Product`/`Unit`/`Location`/
  `Category`.
- **One aggregate per transaction.** A single `SaveChanges`/transaction mutates one
  aggregate root. Multi-aggregate fan-out (cook issuing N `Consume` calls, Intake's
  commit orchestration) is the *only* sanctioned exception, and even there each
  downstream call is its own transaction, driven by an application service — never one
  cross-aggregate save.
- **Invariants stay inside the aggregate.** Mutation goes through guarded methods on
  the root — private constructor + static `Create` factory + methods like
  `UpdateName`/`SetExpiryWarningDays` that validate before assigning (house style, e.g.
  `Household`). No public setters, no reaching into a child collection from outside.

## Gate 3 — Household tenancy

- Every aggregate root carries `household_id`, and every query filters by it. A
  repository method that can return rows without `WHERE household_id = @id` — relying
  on Postgres RLS alone, or vice versa — is a tenant-isolation bug. **Both** the
  repository filter and RLS (`SET app.household_id`) must be present as defense-in-depth.
- New tables that hold household-scoped data need RLS policies wired up consistent with
  existing migrations.
- `user_id` rides along on journal/audit rows for attribution.

## Gate 4 — The single consumption primitive

- **Every stock removal flows through `ProductStock.Consume(quantity, unit, reason,
  sourceRef?)`.** Any new code that decrements `StockEntry.quantity` directly, or writes
  a removal-shaped journal row outside `Consume`, is a violation — even if it "only"
  handles one new case.
- **`reason` (Consumed / Discarded / Correction) and `source_type` (Intake / Manual /
  Cook / …) are separate axes** — don't conflate "why stock left" with "what triggered
  it."
- Inventory stays ignorant of recipes and substitutions — that vocabulary belongs to
  Recipes' cook orchestration, which issues plain `Consume` calls per resolved ingredient.
- Quantity-neutral lot transitions (transfer, freeze/thaw, open) update `StockEntry`
  state directly and write **no** journal row — don't "fix" that by routing them through
  `Consume` or adding journal entries for them.

## Gate 5 — AI as untrusted external function

- Model calls happen **server-side only**, through the `ChatClient` abstraction. No
  client-side calls, no API key reachable from the browser.
- AI output lands in a **staging aggregate** (`ImportSession`/`ImportLine`,
  `MealPlanProposal`) as a proposal — raw model output kept in `raw_parse` jsonb for
  provenance — and *only* an explicit user confirmation may trigger writes into
  Inventory/Catalog/Pricing/MealPlan. Any path that lets AI output write straight into
  a core aggregate without that review step is a violation, regardless of confidence score.
- Only user-*resolved*, typed fields commit. Don't promote raw AI fields straight into
  typed columns.

## Gate 6 — Hypermedia-first UI: no SPA, no Node

- No front-end framework, bundler, `package.json`, or `node_modules`. Flag any new JS
  dependency that isn't htmx or Alpine, and any build step that isn't `dotnet`/
  `dotnet watch`.
- Server-driven interaction is **htmx fragment swaps**; local/draft client state is
  **Alpine `x-data`**. Hand-rolled `fetch` + DOM manipulation that duplicates what
  either library does is a smell.
- The server renders domain state as HTML directly — there is no client-side shadow
  model of domain data. JS that recomputes something the server already computed
  (fulfillment %, cost per serving, totals) is exactly the drift this rules out.
- **Component library is the single source of truth.** `src/Plantry.Web/Pages/Dev/Index.cshtml`
  is the definitive catalogue of every reusable Razor tag helper/partial and canonical
  CSS pattern. Flag markup that re-implements a pattern already in the library, and any
  new component added directly to a feature page that bypasses the library.

## Gate 7 — Persistence conventions

- PKs are app-generated `uuid` (UUIDv7) — not identity columns, not DB-generated UUIDs.
- Every aggregate root has `household_id`; children reference their parent via a
  composite FK `(household_id, parent_id)` against the parent's `UNIQUE (household_id, id)`.
- One Postgres schema per bounded context; cross-context references are bare IDs with
  **no enforced FK** (hard FKs only within a context/aggregate).
- `created_at`/`updated_at` are `timestamptz` (UTC). Money is `numeric(12,2)`,
  quantity `numeric(12,3)`, conversion factors `numeric(18,6)`.
- Catalog reference data is **soft-deleted** (`archived_at`); journal and
  price-observation tables are **append-only** — corrections are new rows, never
  updates or deletes.
- Enums are C# enums persisted as `text` + `CHECK` constraint, not Postgres `ENUM`.
- Receipt images and recipe photos live in PostgreSQL (`bytea`/large objects), not on
  disk or in object storage.

## Gate 8 — Does this serve the product?

A judgment call — raise as advisory unless egregious:

- Plantry's bet is that **friction is the problem**. Does this change make a common
  flow (logging groceries, checking what's cookable, reviewing an import) slower, more
  manual, or more demanding of user discipline than before?
- Recipes, pantry, and cost should stay connected — features that make the user
  cross-reference them by hand work against the core thesis.
- Watch for drift toward what Plantry deliberately isn't: meal-kit/subscription model,
  social/sharing features, or dependency on barcode scanning / external product databases.

---

## Action tiers

Every finding is classified by the **action** it demands, not just its severity. This is what
lets an autonomous run resolve findings without a human adjudicating the report: each tier maps to
a mechanical next step.

| Tier | Meaning | What the runner does |
|------|---------|----------------------|
| **FIX** | Must be resolved before this change merges. Covers both hard correctness/security/tenancy defects **and** cheap, safe, already-decided quality wins. | Fix it in-loop, then re-run the full gate (build → test → critic). |
| **DEFER** | A real issue, but resolving it is genuinely *open* — see the boundary below. | Auto-file a `bd` issue capturing the finding + a concrete recommendation, then proceed. Never silently dropped. |
| **NOTE** | Informational; nothing to act on (e.g. a pre-existing transitive-dependency warning). | Record in the report only. |

### The FIX vs DEFER boundary

DEFER is for **open questions, not large diffs.** Trigger DEFER only when at least one holds:

- **Contested design decision** — resolving it means choosing between genuinely viable approaches, *and*
  no existing ADR or established pattern settles the choice (see next bullet).
- **Out-of-scope blast radius** — the fix escapes the change under review: it touches another bounded
  context, a schema/migration, or a public contract beyond the current diff's footprint.
- **Missing test infrastructure** — verifying the fix needs a harness that doesn't exist yet (e.g. a
  JS test rig), so it can't be safely closed in-loop.
- **Low confidence** — the reviewer isn't sure the fix is correct or that the finding is real.

**Effort and size are never on their own a reason to DEFER.** "It's a 45-minute refactor" is not an
open question — a large but in-scope, decided, high-confidence change is a **FIX**. Deferring on effort
is how quality rots under automation.

**Resolve apparent design forks against the codebase first.** When a finding *looks* like a contested
decision, check whether an existing ADR or established pattern already makes the call. If it does, cite
it and **FIX** — don't punt a decision to a human that the codebase has already made. Only a fork that
is genuinely unsettled *and* consequential is a DEFER.

**Tie-breaker: when torn between FIX and DEFER, DEFER.** A wrong auto-fix is expensive and can ship
silently; a bead is cheap and reversible.

### Guardrails on FIX (in-loop auto-fix)

- **Scope ceiling.** An auto-fix must stay within the change's existing footprint (the files already in
  the diff, or trivially adjacent). If a "cheap fix" starts spreading to unrelated files, it has become a
  DEFER — file the bead instead of expanding the diff.
- **Re-verify.** Every FIX re-runs the full gate; FIX is bounded by the loop's pass cap. Confident-but-wrong
  fixes are caught by test/critic, not shipped.

### Default tier per gate

These are the *starting* classifications; the FIX/DEFER boundary above decides where a non-blocking
finding actually lands.

| Gates | Default tier |
|-------|--------------|
| 1–5 | **FIX** — correctness/security/tenancy/AI-staging defects always block merge |
| 6 — new JS dependencies, SPA patterns, API key from browser | **FIX** |
| 6 — UI library drift, component-order violations | **FIX or DEFER** per the boundary (cheap & in-scope → FIX; needs a new pattern/component decision → DEFER) |
| 7 | **FIX** — persistence contract violations cause correctness bugs |
| 8 | **DEFER or NOTE** — product-alignment judgment; FIX only if egregious and in-scope |

### Calibration anchor

Hold findings to the bar of a top-tier engineering org: would a strong reviewer let this merge as-is, or
leave a "fix this first" comment? Use this only to *calibrate* how hard to look — the tier definitions
above, not the vibe, decide the action.
