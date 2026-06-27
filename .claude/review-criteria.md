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

## Gate 6 — UI architecture: hypermedia default, bounded reactive islands

The app runs **two UI models** since ADR-020. The default is unchanged; islands are a
bounded exception on three named surfaces. The line between them is the thing most likely
to fray — police it as a bright, boring rule.

- **Default is htmx + Alpine; islands are the bounded exception (ADR-020 §1).**
  Server-driven interaction is **htmx fragment swaps**; local/draft state on non-island
  pages is **Alpine `x-data`**. A client-side reactive **island** is sanctioned for
  *exactly three* surfaces: **Intake review** (`Pages/Intake/Review`), **Meal Planner**
  (`Pages/MealPlan` + editor), **Take Stock** (`Pages/Pantry/TakeStock`). New surfaces do
  **not** default to an island — a new island anywhere else, or porting a fourth surface,
  **reopens ADR-020** and is a finding, not a silent change. "Is this an htmx page or an
  island?" must stay answerable at a glance.
- **The island boundary — domain logic stays server-side (ADR-020 §2/§7).** An island
  owns *only* ephemeral UI state: draft collections, open/closed drawers,
  selected-but-unsaved fields, and derived **display** values that are pure functions of
  state it already holds (counts, sums, progress, enable/disable). The **server owns every
  domain concern** — persistence, fulfillment %, cost/rollup, validation-as-truth, and any
  catalog-default or unit-semantics rule — behind JSON endpoints. **An island that computes
  fulfillment, cost, validation-as-truth, or a catalog/unit-semantics rule client-side is a
  §7 tripwire breach** — it reopens the ADR (record an amendment, never absorb silently).
- **Where the line falls when "is this domain?" is ambiguous (ADR-020 §3 — apply these
  verbatim, don't relitigate per screen):**
  1. A **priority/derivation chain** that picks among competing sources (Intake's
     `ComputePrefill`: `user-resolved > receipt-parsed > product-default`; *"receipt unit
     wins so 2 kg never becomes 2 each"*; *"expiry = today + DefaultDueDays"*) is **domain
     → server**. The island receives the *computed* values and renders them; it must never
     re-derive the chain.
  2. Filling **one** empty field from reference data the island already holds, on user
     interaction (re-select a product → fill unit/location/expiry from that product's
     hydrated defaults, incl. `today + dueDays`) is **UI → island-allowed**. The line:
     applying a single default is UI; owning the priority chain that chooses *between*
     sources is domain. Case 2 must not grow into case 1.
  3. **Validation is mirrored, not moved.** An island may mirror field guards
     (`quantity > 0`, unit/location required, new-product needs name+category) to gate Save
     and show inline hints; the server re-validates every mutation and is **the truth**.
- **Buildless *shipped runtime*, no new shipped dependencies (ADR-020 §6).** The only
  sanctioned reactive tech is **Preact + htm + `@preact/signals`**, vendored as pinned ESM in
  `wwwroot/js/islands/vendor/` with **relative imports**. The rule is about what the **browser
  loads**: the file that runs is the file you read. Flag any: new *shipped* JS dependency
  beyond {htmx, Alpine, the vendored island runtime}; a bundler or transpile of shipped JS;
  `node_modules` / an npm dependency tree on the shipped path; an **import map** (it fights
  Razor `@@` escaping — relative vendoring is the form). **esbuild is the *only* sanctioned
  future build step for shipped code, and adopting it is an ADR-020 amendment, not a silent
  addition.** Vendored `vendor/*.module.js` are pinned third-party — on a bump review the
  version/pin, not the minified body.
- **Test-time Node is allowed; shipped/build Node is not (ADR-020 amended 2026-06-24).** Island
  JS is tested with Node's built-in runner (`node --test`, zero dependency tree) importing the
  ESM modules directly. A `package.json` is permitted **only** as a dev/test manifest — a `test`
  script + `type: module`, **no dependencies** — with `node_modules` gitignored; it must never
  put Node, a dependency tree, or a build on the **shipped** path. So: `node --test` and a
  deps-free test manifest are fine; an npm dependency tree, a bundler, or Node in what ships are
  still **FIX**. Untested island transform logic (factories, draft→POST-body builders, display
  `computed`s) is now a normal testability finding — the rig exists, so "there's no JS test rig"
  is no longer a reason to wave it through.
- **Islands are not a SPA (ADR-020 §5).** No client router; each island mounts per page
  from server-rendered HTML and **dies on navigation**. The server owns routing,
  auth/session, and navigation. A client-owned app shell, a persistent client router, or
  cross-island shared mutable state is out of bounds.
- **Transport & hydration go through the shared seam, not hand-rolled.** Islands read
  server-emitted hydration via `readHydration` and post drafts via `postJson` +
  `readAntiforgeryToken` (`islands/helpers.js`), and import the runtime from
  `islands/runtime.js`. Hand-rolled `fetch`/DOM wiring that duplicates the runtime or
  helpers is a smell (the islands analog of "don't re-implement what htmx/Alpine already
  do"). **`helpers.js` and `runtime.js` are UI/transport only — domain logic must never
  migrate into them either.**
- **The contract seam must stay in sync (ADR-020 consequences).** Each island surface adds
  a server-VM ↔ island-props JSON contract (the hydration payload + the JSON the island
  POSTs back) that pure hypermedia lacks — **no compiler spans it.** A change to the
  server-emitted shape not reflected in the island's consumption (or vice versa) is a real
  defect → FIX. A *missing* contract test for a new island surface is a legitimate DEFER
  (needs JS test infra).
- **Reusable island widgets follow the same reuse discipline as Razor/CSS (below).** A
  reactive widget built inline in one island and then near-duplicated in another (e.g. a
  search-as-you-type picker living as both `ProductSearch` and `DishSearch`) is the JS
  analog of four divergent steppers — extract the shared component before a third copy.
  Conversely, don't force-share widgets whose behaviour genuinely diverges.
- **The component library is the source of truth for *shared, reusable* UI — not an
  inventory of every element.** `src/Plantry.Web/Pages/Dev/Index.cshtml` catalogues the
  cross-cutting building blocks feature pages compose with: reusable Razor tag
  helpers/partials (`<field>`, `_DataGrid`, `_CatChip`) and canonical CSS patterns reused
  across pages (`.card`, `.seg-ctrl`, badges, pills, steppers, `searchable-select`). Its
  purpose is to prevent **divergent re-implementations of the same primitive** — not to
  register every screen.
- **Reuse before you build; extract before you repeat.** When a page needs a UI element,
  check the library for an existing primitive and compose from it — don't reinvent a
  near-duplicate. Conversely, when the same markup is written more than once (or a clearly
  reusable widget is built inline on a feature page), that is a finding: extract it into a
  tag helper / partial / CSS pattern in the library and have the call sites consume it.
  **Duplicated markup is the smell — not the absence of a registry entry.** Four divergent
  steppers (`.qty-stepper`, `.recipe-servings-stepper`, `.rd-serv-stepper`,
  `.sl-qty-stepper`) or several near-identical filter-chip / progress-bar implementations
  are exactly what this rules out.
- **Page-specific layout is not a library component.** A whole-page scaffold
  (`.today-grid`, `.rd-grid`), a feature-screen region (the `.sl-*` shopping rows,
  `.recipe-card`, the intake dropzone/scan states), or any section with a `feature-name-`
  prefix and a single call site belongs on its feature page, *not* in the library.
  **Inclusion test:** a thing earns a library entry only if it is reused across pages or is
  a generic primitive any page could pick up. Do not flag — or require library registration
  for — single-use markup that merely *composes* existing primitives.

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

## Gate 9 — Observability

New code in handlers, application services, and domain event handlers must carry adequate
instrumentation. The rules below apply to **new code only**; retrofitting existing
uninstrumented code is a DEFER (file a bead, don't expand the diff).

- **Structured logging via `ILogger<T>`, injected — never `Console.Write*`.**
  Any class that logs must receive `ILogger<T>` through its constructor. Direct console
  writes are a violation even in one-off or "diagnostic" paths.
- **Log happy-path domain operations at `LogInformation`.**
  Key operations must emit a structured log on their successful path:
  - Intake: import session committed
  - Inventory: stock consumed (including reason and source type)
  - AI pipeline: parse started, parse completed (model, token counts if available)
  - Meal planning: meal plan generated
  - Any other handler or application-service method introduced by the diff that
    represents a meaningful domain state change
- **Log all exception and failure paths at `LogWarning` or `LogError`.**
  Re-throwing or catching an exception without logging is a violation. The log entry must
  include the exception object (so the stack trace is captured) and enough structured
  parameters to identify the operation and entity. Swallowing exceptions silently is a
  correctness defect, not just an observability gap.
- **Custom `ActivitySource` spans for AI model calls.**
  Every call that invokes a language model via `ChatClient` (or any wrapper around it)
  must be wrapped in an `ActivitySource.StartActivity(...)` span, started before the call
  and stopped (via `using` or explicit `Stop`/`Dispose`) after it. AI calls are latency-
  sensitive, expensive, and the most likely failure point — they must be individually
  traceable. An AI call with no enclosing span is a FIX.
- **No PII or secrets in log message parameters.**
  Household names, user emails, API keys, passwords, and receipt raw text must not appear
  as structured log parameters. Log the entity ID or a redacted sentinel instead. A log
  statement that captures a raw email or API key is a security defect (Gate 1 + Gate 9).
- **Do not read observability signals back from the framework in unit tests.**
  Unit tests must not assert on log output by intercepting `ILogger` calls (e.g. via
  `Mock<ILogger<T>>` + `Verify`) unless testing log behavior is the *explicit, stated goal*
  of the test. Using log assertions as a proxy for "did this code path execute" is a test-
  design smell — assert on return values or domain state instead. When log output must be
  verified, use integration tests with a captured log sink, not unit-test mocks.

**Default tier for Gate 9:**

| Scenario | Tier |
|----------|------|
| New handler/service/domain-event-handler introduced by the diff with no `ILogger<T>` injection and no logging | **FIX** |
| New AI model call (`ChatClient` invocation) with no enclosing `ActivitySource` span | **FIX** |
| Exception path (catch or re-throw) with no `LogWarning`/`LogError` | **FIX** |
| PII or secret value in a log message parameter | **FIX** (Gate 1 + Gate 9) |
| Existing, pre-diff code that is uninstrumented and untouched by the diff | **DEFER** — file a bead; do not expand the diff |
| Unit test asserting on `ILogger` calls as a side-effect proxy | **FIX** (unless the test is explicitly about log output) |

---

## Action tiers

Every finding is classified by the **action** it demands, not just its severity. This is what
lets an autonomous run resolve findings without a human adjudicating the report: each tier maps to
a mechanical next step.

| Tier | Meaning | What the runner does |
|------|---------|----------------------|
| **FIX** | Must be resolved before this change merges. Covers both hard correctness/security/tenancy defects **and** cheap, safe, already-decided quality wins. | Fix it in-loop, then re-run the full gate (build → test → critic). |
| **DEFER** | A real issue, but resolving it is genuinely *open* — see the boundary below. | Auto-file a `bd` issue capturing the finding + a concrete recommendation, then proceed. Never silently dropped. |
| **NOTE** | Informational; **no recommended action** (e.g. a pre-existing transitive-dependency warning). A finding with a next step is FIX or DEFER — see *Guardrails on NOTE*. | Record in the report only. |

### The FIX vs DEFER boundary

DEFER is for **open questions, not large diffs.** Trigger DEFER only when at least one holds:

- **Contested design decision** — resolving it means choosing between genuinely viable approaches, *and*
  no existing ADR or established pattern settles the choice (see next bullet).
- **Out-of-scope blast radius** — the fix escapes the change under review: it touches another bounded
  context, a schema/migration, or a public contract beyond the current diff's footprint.
- **Missing test infrastructure** — verifying the fix needs a harness that doesn't exist yet, so it
  can't be safely closed in-loop. (Note: island JS is **no longer** an example — a `node --test` rig
  is sanctioned as of the 2026-06-24 ADR-020 amendment, so "there's no JS test rig" is not a DEFER
  reason for island-logic coverage once that rig has landed.)
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

### Guardrails on NOTE

- **NOTE is only for findings with no recommended action.** If the finding carries a concrete next
  step — add a test, change a line, file a bead — it is a FIX or a DEFER, never a NOTE. Reserve NOTE
  for observations nothing acts on: pre-existing transitive-dependency warnings, deliberate design
  choices, FYI context.
- A finding whose text contains "follow-up", "tracked as", "should later", or "consider …" is
  **mis-tiered as NOTE** — it has named an action. Re-classify it FIX (close it in-loop) or DEFER
  (file the bead, which is what tracks it — not the prose).

### Author acknowledgments do not lower a tier

- An author's in-code comment, `TODO`, commit note, or "known gap / follow-up" annotation carries
  **zero** weight in classification. Tier the finding exactly as if the acknowledgment were absent.
- An acknowledged-but-unaddressed gap with **no tracked bead** is the *worst* case, not a mitigated
  one — close it (FIX) or file the bead (DEFER). Never downgrade to NOTE because the author already
  conceded the problem. Self-acknowledgment is precisely how a blind review gets talked out of a
  finding it would otherwise block on.

### Tiers may not silently soften across passes

- *(Multi-pass loops only.)* If a finding is classified at a **lower** tier than a related finding
  flagged in an earlier pass, the report must state **why the earlier concern is now resolved** — not
  merely renamed or acknowledged. Renaming a misleading test does not resolve "the behavior is
  untested." Absent a stated resolution, carry the earlier (higher) tier forward.

### Default tier per gate

These are the *starting* classifications; the FIX/DEFER boundary above decides where a non-blocking
finding actually lands.

| Gates | Default tier |
|-------|--------------|
| 1–5 | **FIX** — correctness/security/tenancy/AI-staging defects always block merge |
| 6 — new *shipped* JS dep / npm dependency tree / bundler / import map / Node or a build on the shipped path; island outside the three sanctioned surfaces; SPA shell or client router | **FIX** (test-time `node --test` + a deps-free test manifest are explicitly allowed — not a finding) |
| 6 — §7 tripwire breach (domain logic computed inside an island); contract-seam divergence between server VM and island props | **FIX** — and a §7 breach also reopens ADR-020 (record an amendment) |
| 6 — UI library drift, divergent Razor/CSS or island widgets, missing contract test for a new island surface | **FIX or DEFER** per the boundary (cheap & in-scope → FIX; needs a new shared component or JS test infra → DEFER) |
| 7 | **FIX** — persistence contract violations cause correctness bugs |
| 8 | **DEFER or NOTE** — product-alignment judgment; FIX only if egregious and in-scope |
| 9 — new handler/service with no `ILogger<T>`, new AI call with no `ActivitySource` span, exception path with no `LogWarning`/`LogError`, PII in log parameters | **FIX** |
| 9 — existing uninstrumented code untouched by the diff | **DEFER** — file a bead; do not expand the diff |

### Calibration anchor

Hold findings to the bar of a top-tier engineering org: would a strong reviewer let this merge as-is, or
leave a "fix this first" comment? Use this only to *calibrate* how hard to look — the tier definitions
above, not the vibe, decide the action.
