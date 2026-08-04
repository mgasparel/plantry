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
  own schema (`identity`, `catalog`, `inventory`, `intake`, `market` (schemas: `pricing`,
  `deals`), `recipes`, `meal_planning`, `shopping`). If `Plantry.Recipes` needs Inventory data, it
  calls Inventory's application service or reads its read model — it never queries
  `inventory.*` tables directly. The `housekeeping` schema (the `Dismissal` tombstone table) is
  the one exception to "each schema has an owning bounded context": ADR-024 Phase A dissolved the
  Housekeeping context — its 7 read-only detectors and `Dismissal` now live directly in the
  composition read layer (`Plantry.Web`, ADR-021 shape), which legitimately reads every schema, so
  there is no context left to protect `housekeeping`'s boundary specifically. This is not a
  precedent for other schemas to gain the same exemption without a similar ADR.
- **Cross-context references are IDs only, never embedded entities.** A
  `Recipe.Ingredient` holds a `ProductId`, not a `Product`. Catalog is the universal
  upstream supplier — nothing downstream mutates a `Product`/`Unit`/`Location`/
  `Category`.
- **One aggregate per transaction.** A single `SaveChanges`/transaction mutates one
  aggregate root. Multi-aggregate fan-out (cook issuing N `Consume` calls, Intake's
  commit orchestration) is the *only* sanctioned exception, and even there each
  **downstream** call (i.e. after the cook/commit anchor has already saved) is its own
  transaction, driven by an application service — never one cross-aggregate save.
  This "each downstream call is its own transaction" clause governs calls *after* an
  anchor commit; it does not forbid the narrow, separately-named exception where the
  anchor commit itself co-commits with another aggregate sharing the same DbContext/
  connection (e.g. `CookRecipe`'s `Recipe.SetYield` co-committing with the `CookEvent`
  anchor — see ADR-010's 2026-07-25 amendment). Any such co-commit must
  be named in ADR-010 as a bounded exception, not assumed by analogy.
- **Invariants stay inside the aggregate.** Mutation goes through guarded methods on
  the root — private constructor + static `Create` factory + methods like
  `UpdateName`/`SetDefaultDueDaysAfterFreezing` that validate before assigning (house
  style, e.g. `Household`). No public setters, no reaching into a child collection from
  outside.

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
  Inventory/Catalog/Market/MealPlan. Any path that lets AI output write straight into
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
  defect → FIX. A *missing* contract test for a new island surface is a FIX — build the
  test-layer rig in-case — unless its shape is a genuinely unprecedented design call
  (`needs-human`, naming the contest).
- **Reusable island widgets follow the same reuse discipline as Razor/CSS (below).** A
  reactive widget built inline in one island and then near-duplicated in another (e.g. a
  search-as-you-type picker living as both `ProductSearch` and `DishSearch`) is the JS
  analog of four divergent steppers — extract the shared component before a third copy.
  Conversely, don't force-share widgets whose behaviour genuinely diverges.
- **The component library is the source of truth for *shared, reusable* UI — not an
  inventory of every element.** `src/Plantry.Web/Pages/Dev/Index.cshtml` catalogues the
  cross-cutting building blocks feature pages compose with: reusable Razor tag
  helpers/partials (`<field>`, `_DataGrid`, `_FilterChip`) and canonical CSS patterns reused
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
instrumentation. The rules below apply to **new code only**. Pre-existing uninstrumented
code that the diff touches or that sits on the changed call path is instrumented as part
of the FIX; unrelated uninstrumented code is a NOTE — archaeology belongs to deliberate
sweeps, not per-case beads.

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
| Existing, pre-diff uninstrumented code on the changed call path | **FIX** — instrument it in-case |
| Existing, pre-diff uninstrumented code unrelated to the diff | **NOTE** — no per-case bead; deliberate sweeps own archaeology |
| Unit test asserting on `ILogger` calls as a side-effect proxy | **FIX** (unless the test is explicitly about log output) |

## Gate 10 — Test quality & determinism

Gate 1 asks *"is there coverage?"* — Gate 10 governs whether the tests that exist are
**sound**: deterministic, at the right altitude, and actually asserting behavior. A test
that passes or fails on wall-clock time, machine culture, or scheduling luck is worse than
no test — it erodes trust in the whole suite and trains reviewers to rerun until green.
Motivating case: six Today planned-band tests that intermittently missed
because the fixture seeded a meal under `DateTime.UtcNow` while the page resolved "today"
under `LocalDateTime`; no prior gate looked at test *quality*, so nothing flagged it.

### A. Determinism / anti-flake (default **FIX** — a nondeterministic test erodes the gate itself)

- **Ambient time.** A test or fixture that reads `DateTime.Now` / `DateTime.UtcNow` /
  `DateTimeOffset.Now` / `DateOnly.FromDateTime(DateTime.Now)` at construction or assertion
  time instead of injecting the house-style `IClock` (prod: `SystemClock.Instance`; tests: a
  fixed/fake clock). **The UTC-vs-local trap specifically** (the ouvi bug): a fixture seeds
  date-keyed data under one zone while the SUT resolves "today"/"now" under another, so the
  test straddles a day boundary near midnight. **Rule:** a fixture must key date/time-scoped
  data off the *same* clock the SUT reads, and that clock must be **fixed** in the test — a
  planned-band or expiry test may never depend on when the CI job happens to run.
- **Shared mutable state / ordering dependence.** `static` fixture singletons that carry
  mutation between tests or regenerate GUIDs per call; tests that depend on execution order,
  on xUnit collection/parallel scheduling, or on a class-fixture instance leaking state into
  the next test. Each test must arrange its own world and pass in isolation and in any order.
- **Unseeded randomness.** `Guid.NewGuid()` or `Random` where the value is later asserted or
  determines ordering — use stable, explicit fixture IDs (fixed `Guid`s / a seeded generator)
  so the assertion is reproducible.
- **Real waits & timing.** `Thread.Sleep` / `Task.Delay` / wall-clock polling used as a
  synchronization mechanism, or an assertion that relies on operation latency. Drive time
  through the injected clock or an awaited signal, never by sleeping.
- **Unordered-collection assumptions.** Asserting on `.First()` / indexer of a `HashSet`/
  `Dictionary`, on DB rows fetched without an `ORDER BY`, or on `querySelectorAll()[i]` where
  DOM order isn't guaranteed. Assert against an explicitly ordered projection or match by key.
- **Environment coupling.** Culture-sensitive parse/format (decimal separators, date formats,
  casing) without `CultureInfo.InvariantCulture`; real network / filesystem / environment-
  variable / clock dependence in a test that claims to be hermetic. An L1/L2 unit test must
  not touch the machine it runs on.
- **Async misuse in tests.** A missing `await`, a fire-and-forget task, or `.Result` / `.Wait()`
  that can deadlock or let the assertion race the operation — the test may pass before the work
  finishes.

### B. Pyramid altitude (default **FIX or DEFER** per the boundary)

The suite follows the L1–L5 taxonomy in `docs/PHASE-1-PLAN.md`: **L1 domain unit is the
majority**; L3 integration is one suite per context; **L4 (`WebApplicationFactory`) and L5
(Playwright + `Aspire.Hosting.Testing`) are deliberately few** because they are slow.

- **Inversion = flag.** Pure domain logic, invariants, or a derivation chain covered *only*
  through an expensive L4/L5 path when a fast L1/L2 unit test would pin the same assertion —
  push it down. Fulfillment rollup, FEFO/expiry ordering, unit-conversion resolution, and the
  Intake `ComputePrefill` priority chain (`user-resolved > receipt-parsed > product-default`)
  belong at **L1/L2**, not asserted solely via a rendered fragment or a booted service graph.
- **Don't invert the other way either.** Cross-cutting wiring, routing, RLS isolation, EF
  mappings, and rendered-fragment contracts are *supposed* to live at L3/L4/L5 — proving them
  with heavily mocked "unit" tests that stub out the very seam under test proves nothing real.
- Match the tier to what is actually being verified; a new test at the wrong altitude is a
  finding even when it is green.

### C. Test value / anti-patterns (**FIX** when in-scope and clearly wrong; else **DEFER**)

- **Assertion-free or tautological tests** — a test that exercises code but asserts nothing,
  or asserts a constant against itself, is coverage theatre.
- **Change-detector / over-mocked tests** that restate the implementation's call sequence
  (asserting *on the mock* — "was method X called with Y") instead of asserting on the SUT's
  return value or resulting domain state. This generalizes Gate 9's ban on
  `Mock<ILogger>.Verify` as an execution proxy (cross-reference it): a test coupled to *how*
  the code runs rather than *what* it produces breaks on every refactor and catches no bug.
- **Over-specified / brittle assertions** coupling to incidental detail — exact whitespace,
  full-HTML string equality, an entire serialized blob — where a targeted selector or single
  value would prove the behavior without breaking on cosmetic edits.
- **Hidden or invisible paths.** Branching/loops inside a test that silently skip assertions
  down one path; "magic fixtures" so large the Arrange is unreadable and no one can tell what
  is actually under test.
- **Mutation-gap hook (ties to Gate 1).** A test that would still pass if the behavior it
  claims to cover were broken — an obvious surviving mutant per `stryker-config.json`. If
  flipping the guard or the operator under test wouldn't turn the test red, the test doesn't
  assert the behavior.

**Default tier for Gate 10:**

| Scenario | Tier |
|----------|------|
| Test/fixture reads ambient `DateTime.Now`/`UtcNow`/`DateOnly.FromDateTime` instead of an injected fixed clock | **FIX** |
| Fixture seeds date-keyed data under a different zone/clock than the SUT resolves "today"/"now" from (UTC-vs-local trap) | **FIX** |
| Asserted/order-determining value comes from `Guid.NewGuid()`/`Random`, or `Thread.Sleep`/`Task.Delay` used as sync | **FIX** |
| Assertion on unordered-collection `.First()`/indexer, DB rows without `ORDER BY`, or DOM `[i]` without guaranteed order | **FIX** |
| Culture-sensitive parse/format without `InvariantCulture`; real network/fs/env in a "hermetic" test; async misuse (missing `await`, `.Result`) | **FIX** |
| Pyramid inversion — pure domain logic pinned *only* by an L4/L5 path a fast L1/L2 test would cover | **FIX** — drop the assertion down, building any missing fixture/harness in-case |
| Assertion-free/tautological, change-detector/over-mocked, or brittle over-specified test | **FIX** — rewrite it in-case, wherever it lives |

---

## Action tiers

Every finding is classified by the **action** it demands, not just its severity. This is what
lets an autonomous run resolve findings without a human adjudicating the report: each tier maps to
a mechanical next step.

| Tier | Meaning | What the runner does |
|------|---------|----------------------|
| **FIX** | Must be resolved before this change merges. Covers both hard correctness/security/tenancy defects **and** cheap, safe, already-decided quality wins. | Fix it in-loop, then re-run the full gate (build → test → critic). |
| **DEFER** | A real issue the loop **cannot resolve autonomously** — a `needs-human` decision or a named `cannot-complete` blocker; see the boundary below. Its recommendation must be tree-verified before it's written as bead-ready text — see "Verifying a DEFER recommendation" below. | In the autonomous pipeline: hand the batch of DEFERs to the **fable-arbiter** (`.claude/agents/fable-arbiter.md`), which rules each one FIX-IN-CASE / FILE / ABSORB / DROP — a bead is filed only on a FILE ruling. Outside the pipeline (standalone review): file the `bd` issue directly. Never silently dropped either way. |
| **NOTE** | Informational; **no recommended action** (e.g. a pre-existing transitive-dependency warning). A finding with a next step is FIX or DEFER — see *Guardrails on NOTE*. | Record in the report only. |

### The FIX vs DEFER boundary

**Owner directive (2026-07-28): DEFER is for work the loop cannot complete autonomously —
never for work that merely escapes the diff.** The worker's job is to get the issue merged
with every real finding resolved; expanding the diff to do that is expected, not a
violation. Effort, size, and footprint are never boundaries: a fix is re-verified by the
full gate for free, while a bead taxes the owner's backlog and re-manufactures the same
finding in every later case that touches the seam.

Trigger DEFER only when at least one holds. There are exactly **two** triggers — each
defined with a boundary test, because a bare label invites laundering "this is a lot of
work" through it:

- **`needs-human`** — resolving it requires a decision that is genuinely the owner's:
  product/UX behaviour, a numeric threshold/cutoff, or a consequential design fork that no
  existing ADR, precedent, or house pattern settles (see "Resolve apparent design forks"
  below). *Boundary test:* name the actual fork (option A vs option B, what makes both
  viable, and why an agent picking either would be overreach). **Having options is not
  ambiguity** — when any defensible choice exists (an ADR, a precedent, the way the nearest
  analog already works, or a plain house convention), make the call, record it in the case
  comment, and FIX.
- **`cannot-complete`** — the fix genuinely cannot be built *and verified green* within
  this case: it needs external resources/credentials the pipeline lacks, a production data
  operation, work covered by a **load-bearing spec scope lock** (see below), or the
  attempted fix failed the gate in a way the pass budget cannot absorb. *Boundary test:*
  state specifically what was attempted or what is missing. "It would touch other
  files/contexts/schemas" is **never** sufficient — that is a description of the fix, not
  a blocker.

**Retired triggers (2026-07-28)** — do not use these labels:
- `out-of-scope` is retired. Footprint escape alone defers nothing; fix it and let the
  gate verify it.
- `low-confidence` is retired as a *deferral*. A finding the reviewer cannot confirm is a
  **verification task, not a bead**: state the check that would settle it, run that check
  in-case (the worker has the tree and the test suites), then FIX or discard. Only when
  the settling check is itself impossible in-case does it defer — as `cannot-complete`,
  naming the missing check.

**"Missing test infrastructure" is NOT a trigger.** If a test needs a helper, fake, fixture, or
harness that doesn't exist: **building it is part of the FIX.** The loop is serial — infra built
in one case is inherited by every later case, whereas deferring it manufactures the same defer in
every subsequent case that touches the seam (observed: the TimeProvider seam deferred 3+ times in
one week before this rule existed). The one genuine escape already has a trigger: infra whose
*shape* is a consequential, unprecedented design call (a production seam with no analog, a
harness with no ADR) is `needs-human` naming the actual contest. If a fix ships while its
verification is deferred, the filed bead MUST list which shipped changes are unverified pending
it — verification debt is tracked, never silent.

**Reuse-first for test helpers.** Before creating any test helper, fake, or fixture, search the
test tree for prior art. Creating a duplicate of an existing helper (same shape, same purpose —
regardless of name) is a **FIX**, not a NOTE: delete the new copy and consume or extend the
existing one. This is the test-tree mirror of the UI component-library rule in
`.claude/CLAUDE.md`.

**Effort and size are never on their own a reason to DEFER.** "It's a 45-minute refactor" is not an
open question — a large but in-scope, decided, high-confidence change is a **FIX**. Deferring on effort
is how quality rots under automation.

**Resolve apparent design forks against the codebase first.** When a finding *looks* like a contested
decision, check whether an existing ADR or established pattern already makes the call. If it does, cite
it and **FIX** — don't punt a decision to a human that the codebase has already made. Only a fork that
is genuinely unsettled *and* consequential is a DEFER.

**Tie-breaker: when torn between FIX and DEFER, FIX.** Every fix re-runs the full gate —
build, all suites, a fresh critic — so a wrong fix is caught before merge, not shipped. A
bead is not cheap: it lands on the owner's triage queue, and deferring work an agent could
do is the failure mode this boundary exists to prevent.

### Verifying a DEFER recommendation

Every reviewer that can produce a DEFER — the interactive `/plantry-code-review` skill,
`plantry-preflight` Stage 3, and the autonomous pipeline's inline critic template in
`.claude/agents/implement-ticket-worker.md` — is bound by this check. It lives here, in the
canonical DEFER definition, so every consumer inherits it rather than each reviewer carrying
its own copy.

A reviewer sees only the diff under review — but a DEFER's *recommendation* often describes
work in code the diff never touched. Filed verbatim, an unverified recommendation is a guess
dressed up as a spec: it gets filed as a bead and worked as though it were scoped. Four beads
that motivated this requirement each shipped a recommendation pointing the opposite direction
from its own (correct) finding once someone looked one level out of the diff.

Before writing a DEFER's recommendation as bead-ready text, run whichever of these checks apply
to it:

1. **Names a project/folder/file that will host the new work** — confirm it exists and can
   actually do the job (has the right references, can see the source it needs to check,
   matches its established purpose). Don't recommend a `.cshtml`-parsing test in a project whose
   assembly never references `Plantry.Web`.
2. **Proposes a new guard/check** — run it mentally or literally against the current tree and
   state the finding count in the recommendation. A guard that would fail on arrival must say so
   and propose a baseline instead of being filed as though the tree is already clean.
3. **Proposes a rename or a new naming convention** — grep the existing family first and state
   in the recommendation which side is already conforming. Don't recommend renaming the
   compliant member of a pattern.
4. **Cites a count or enumeration** ("~18 factories", "6 call sites") — derive the number from
   the tree and state the command used. An eyeballed count is not evidence.
5. **Calls something an unsettled design fork** — confirm the code does not already implement
   one side of it. A trade the codebase has already made is a fact to cite (see "Resolve
   apparent design forks against the codebase first" above), not a fork left open to defer.

**Escape hatch — `recommendation-unverified`.** When a check above applies but can't be run
within the review's time budget, don't skip it silently — tag the finding
`recommendation-unverified` in its DEFER line. This changes what the filed bead *is*: a bead
tagged `recommendation-unverified` is a **lead** ("this is worth someone looking at"), not a
**spec** ("here is the fix"). Carry the tag into the bead as a label so whoever eventually works
it knows to re-derive the recommendation from the tree rather than implement it verbatim. In the
autonomous pipeline, the critic marks the tag with a `[recommendation-unverified]` suffix on the
DEFER line (see the critic template's output format in
`.claude/agents/implement-ticket-worker.md`), and the arbiter treats any so-tagged finding as a
lead, never a FIX-IN-CASE spec.

Tree verification is a property of the *recommendation*, not of the filing mechanics — a wrong
recommendation is wrong the moment it's written, regardless of what downstream process later
turns it into a bead. Checking it inside the review pass that already has the diff and the tree
open catches the error at its source, with the least duplicated context; a separate verification
step inserted between review and `bd create` would run after the reviewer had already committed
to a wrong finding, and would need to reconstruct — from scratch, in a different process —
context the review pass already has for free. The added cost (tree-reading in a pass that's
deliberately diff-scoped) is bounded: it only fires per-DEFER, and only for the finding shapes
the five checks above name, not for every finding.

Sanity-checked against the four filed beads that motivated this requirement: checks 1 and 2
would have caught a recommendation naming a phantom project reference and a proposed guard
that failed on arrival with 44 unmatched tokens; check 3 would have caught an inverted
rename (the recommendation targeted the already-conforming member of the pattern); check 5
would have caught an "unsettled fork" that `docker-compose.prod.yml` already resolves;
check 4 would have caught an ungrounded factory count, and check 5 an extension-method
recommendation contradicting the house convention already in `Infrastructure/`.

### Spec scope locks

A ticket may forbid work inside its own footprint ("do not deduplicate here", "reused
verbatim", "do not consolidate"). Locks are legitimate but expensive — every lock
manufactures a near-certain future finding — so they carry obligations for both the spec
author and the reviewer:

- **Spec authors must declare the lock's kind.** `load-bearing`: the lock protects a safety
  argument (the canonical example: a byte-identity CSS-consolidation move whose review
  safety depended on nothing else changing, so its spec forbade any dedup during the move).
  `hygiene`: mere scope tidiness.
  Hygiene locks are discouraged — the default for a small adjacent tidy is to include it
  in-case as a separate commit rather than lock it out.
- **A lock that defers known work must cite or create its companion bead at spec time.**
  The deferred work enters the tracker once, deliberately, when the deferral is decided —
  not later, "re-discovered" by a critic as if new.
- **Reviewers honor locks and route around them.** A finding covered by a lock is a DEFER
  whose recommendation names the lock and its companion bead; in the pipeline the arbiter
  ABSORBs it into that bead. A lock with no declared kind and no companion bead is itself
  worth a NOTE naming the gap.
- **Load-bearing locks are never overridden** — not by the reviewer, not by the arbiter,
  regardless of how cheap the locked work looks.

### Guardrails on FIX (in-loop auto-fix)

- **No footprint ceiling (owner directive 2026-07-28).** An auto-fix may expand the diff —
  other files, other bounded contexts, schema/migrations, new test infrastructure — when the
  change is decided and the gate can verify it. The only hard ceilings are **load-bearing
  spec scope locks** and **`needs-human` decisions**; hitting either mid-fix converts the
  finding to DEFER with that trigger.
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
| 6 — UI library drift, divergent Razor/CSS or island widgets, missing contract test for a new island surface | **FIX** — swap to canonical markup / extract the primitive / build the contract test in-case; DEFER as `needs-human` only for a genuinely unprecedented shared-component design call |
| 7 | **FIX** — persistence contract violations cause correctness bugs |
| 8 | **DEFER or NOTE** — product-alignment judgment; FIX only if egregious and in-scope |
| 9 — new handler/service with no `ILogger<T>`, new AI call with no `ActivitySource` span, exception path with no `LogWarning`/`LogError`, PII in log parameters | **FIX** |
| 9 — existing uninstrumented code on the changed call path | **FIX** — instrument in-case; unrelated archaeology is a **NOTE** |
| 10 — determinism / anti-flake (ambient time, UTC-vs-local fixture trap, unseeded randomness, `Sleep`/`Delay` as sync, unordered-collection assumptions, culture coupling, async misuse) | **FIX** — a nondeterministic test erodes the gate itself |
| 10 — pyramid altitude (L4/L5-only coverage of pure domain logic) & test-value anti-patterns (change-detector/over-mocked, tautological, brittle) | **FIX** — drop the assertion down / rewrite in-case, building any needed test-layer harness as part of it |

### Calibration anchor

Hold findings to the bar of a top-tier engineering org: would a strong reviewer let this merge as-is, or
leave a "fix this first" comment? Use this only to *calibrate* how hard to look — the tier definitions
above, not the vibe, decide the action.
