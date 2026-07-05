---
name: plantry-preflight
description: >-
  Run the full pre-flight gate before a commit/PR — build the solution, run all
  test projects (with per-assembly coverage), enforce tiered coverage floors,
  scan testing conventions, then run the plantry-code-review skill — and write a
  single consolidated report to ./.preflight/{timestamp}-{branch}.md. Stops at
  the first failing stage; later stages do not run if an earlier one fails. USE
  FOR: "run preflight", "am I ready to commit/push", "pre-PR check", "build and
  test and review this branch". DO NOT USE FOR: running just one of these steps
  in isolation (use `dotnet build`, `dotnet test`, or the plantry-code-review
  skill directly), or reviewing code outside this repo.
license: MIT
metadata:
  author: plantry
  version: "0.2.0"
---

# Plantry pre-flight

A single gate that chains, in strict order — **build → test (+coverage) →
coverage floors → testing conventions → code review** — and stops at the first
one that fails. Each stage's output feeds the same report; a failing stage is
the last thing written to it.

**Why coverage lives here.** Coverage is no longer collected on every PR. Under
strictly serial development (ADR-016 amended 2026-06-20) the fast per-PR gate
(`.github/workflows/ci.yml`) is build + unit + architecture + island-JS only; the slow suite
**and the coverage gate** moved to the release-tag pipeline
(`.github/workflows/release.yml`: ReportGenerator + CodeCoverageSummary over
unit + integration + E2E). That means a coverage regression is now invisible
until release time. **This pre-flight is the earliest place a coverage or
testing-convention regression can be caught** — it is not a mirror of per-PR CI,
it is the first line of defence ahead of the release gate. To keep pre-flight's
numbers consistent with the release gate, coverage here is aggregated with the
**same engine** (ReportGenerator), pinned as a local dotnet tool.

## Procedure

1. Determine the report path before doing anything else:
   - Branch: `git branch --show-current` (run from `code/`).
   - Timestamp: `yyyyMMdd-HHmmss` in local time.
   - Path: `./.preflight/{timestamp}-{branch}.md` (relative to `code/`). Create
     the `.preflight` directory if it doesn't exist.
   - Sanitize the branch name for the filename the same way you would for a
     review report (replace `/` and other path-unsafe characters with `-`).

2. **Stage 1 — Build.** Run `dotnet build Plantry.sln` from `code/`.
   - Capture the full output. Pull every analyzer/compiler warning line
     (`warning <CODE>: ...`) into its own list — don't just keep the tail
     summary, the warning lines scroll past it.
   - If the build fails (non-zero exit, any `error` lines): write the report
     now (see *Report on early stop* below) with status **FAILED — build**,
     include the full build output (or at minimum every `error` line with
     surrounding context) and whatever warnings were emitted before the
     failure, and **stop — do not run any later stage.**
   - If it succeeds, record the warning list and continue.

3. **Stage 2 — Test (+ coverage collection).** Run
   `dotnet test Plantry.sln --collect:"XPlat Code Coverage" --results-directory .preflight/coverage`
   from `code/` (this covers `Plantry.Tests.Unit`, `Plantry.Tests.Integration`,
   `Plantry.Tests.E2E`, `Plantry.Tests.Web`, and `Plantry.Tests.Architecture`).
   The `--collect` flag adds coverage collection to the *same* run — do not run
   the suite twice. `coverlet.collector` is already referenced by every test
   project, so no project change is needed to collect; each test project emits
   one `coverage.cobertura.xml` under `.preflight/coverage/<guid>/`.
   - Capture per-project **executed/passed/skipped** counts and the names of any
     failing tests with their failure messages/stack traces. `Plantry.Tests.E2E`
     boots a live Aspire stack (Docker + web app); it is part of the gate, not
     optional — do not narrow the run to a subset of projects or apply a category
     filter to skip it.
   - If any test fails or a test project fails to run: write the report now
     with status **FAILED — tests**, including Stage 1's warning list (build
     passed) and the full test failure detail, and **stop — do not run any
     later stage.**
   - A required suite that **executed zero tests** (skipped, filtered out, or its
     fixture failed to start — e.g. Docker not running for the E2E fixture) is a
     **FAILED — tests**, not a pass: a written-but-unexecuted test verifies nothing.
     Record it as such with the reason, and stop. Start Docker / fix the fixture and
     re-run rather than reporting a green gate over a skipped suite. (This is a
     *runtime execution-count* check at the suite level; it is distinct from the
     Stage 2.5 *static* scan for individual skipped tests — see there.)
   - **Stage 2b — Island JS tests (ADR-020 amended 2026-06-24).** After `dotnet
     test` passes, run `node --test src/Plantry.Web/wwwroot/js/islands/__tests__/**/*.test.js`
     (or equivalently `npm test`) from the solution root. No `npm install` is needed
     — there are zero dependencies; `node --test` runs directly. Capture the pass
     count summary (`tests N / pass N / fail N`). If any test fails or zero tests
     execute: write the report with status **FAILED — tests (JS)** and stop. Include
     the node output verbatim. This suite is part of the gate as of the rig landing
     in bead plantry-2zvm.11; skipping it is not a valid option.
   - **Stage 2c — Coverage floors.** Only after every test suite above is green.
     Aggregate the per-project cobertura files into per-assembly line coverage
     and enforce the tiered floors below.
     1. Restore + run ReportGenerator (pinned local tool — see
        `.config/dotnet-tools.json`):
        ```
        dotnet tool restore
        dotnet tool run reportgenerator \
          "-reports:.preflight/coverage/**/coverage.cobertura.xml" \
          "-targetdir:.preflight/coverage/report" \
          "-reporttypes:JsonSummary"
        ```
        Read per-assembly line % from `.preflight/coverage/report/Summary.json`
        (`coverage.assemblies[].{name,coverage}`). Do **not** hand-parse the raw
        cobertura files: the same product assembly is instrumented by several
        test projects (unit + integration + E2E), and correct aggregation needs
        the line-hit **union** across files — that is exactly what ReportGenerator
        does, and it is the same engine the release gate uses, so the numbers are
        computed the same way. (Absolute per-assembly numbers can still differ
        from the release gate: pre-flight aggregates the *full* solution, whereas
        release collects only unit + integration + E2E. The floors below were
        measured from pre-flight's own full-solution baseline, so the gate stays
        self-consistent.)
     2. Classify each `Plantry.*` product assembly into a tier by **rule**, not a
        hand-maintained list:
        - `*.Infrastructure` → **infra** tier.
        - `Plantry.Web`, `Plantry.AppHost`, `Plantry.ServiceDefaults`,
          `Plantry.Migrator` → **advisory** (report the number, never fail).
          (These are thin hosts / entrypoints; the release gate likewise excludes
          them from the coverage threshold.)
        - every other `Plantry.*` product assembly → **domain** tier.
     3. Apply the floor for each gated (domain/infra) assembly using the
        **ratchet floor table** in the appendix. The gate **fails** when any
        gated assembly's measured line % is **below its floor** (compare at whole
        %: fail when `floor(measured) < floor`). If any domain or infra assembly
        is below its floor, write the report with status **FAILED — coverage**
        and stop — do not run later stages. Otherwise record the coverage table
        and continue.
     4. **Ratchet maintenance (do this on every green coverage run, before
        continuing).** For each gated assembly whose `floor(measured)` **exceeds**
        its current recorded floor, raise the recorded floor in the appendix table
        to `floor(measured)`, capped at the tier target (85 domain / 60 infra);
        never lower a floor. Once a floor reaches its tier target it is just the
        target and drops off the exceptions table. Note any raises you made in the
        report's coverage section so the SKILL.md edit is visible in the diff.
   - If everything passes (every suite executed, zero failures, every gated
     assembly at/above its floor), record the summaries and continue.

4. **Stage 2.5 — Testing conventions.** A static scan of `tests/` — no test code
   is added or run here; this is a pre-flight-only check. Skip it on any earlier
   early stop (mark SKIPPED). Hard rules (a violation is **FAILED — conventions**
   — write the report and stop before code review):
   - **Project naming/placement:** every test project is named `Plantry.Tests.*`
     and lives under `tests/`. A test project (references xunit / `Microsoft.NET.Test.Sdk`)
     found outside `tests/`, or a `tests/` project not matching `Plantry.Tests.*`,
     is a violation.
   - **Required framework refs:** every `tests/**/*.csproj` references all of
     `xunit`, `Microsoft.NET.Test.Sdk`, and `coverlet.collector` (the last is what
     makes Stage 2c's coverage collection work — a test project missing it
     silently drops out of the coverage numbers).
   - **No un-reasoned skipped tests:** no `[Fact(Skip = …)]`, `[Theory(Skip = …)]`,
     or `[Ignore]` in `tests/**/*.cs` whose skip string lacks an inline tracked
     reason (a bead id, e.g. `Skip = "plantry-xxxx: flaky under CI"`). A skip
     **with** a bead reference is allowed (surface it as an advisory note); a skip
     with no tracked reason is a hard fail. Today there are **zero** skipped tests,
     so this is a regression guard.
     - *How this differs from Stage 2's zero-tests rule:* Stage 2 is a **runtime**
       check — a whole suite that executed 0 tests fails there. This is a **static**
       check for **individual** `Skip`/`Ignore` attributes in source, which a suite
       can carry while still executing >0 tests (so Stage 2 would not catch them).
       The two are complementary; neither double-counts the other.
   - Advisory (report, never fail): assertion-style smells such as a test method
     with no assertion. Detection is unreliable, so flag and move on — never fail
     on it.

5. **Stage 3 — Code review.** Invoke the `plantry-code-review` skill. It writes
   its own report — note that path, and
   pull its overall pass/fail judgement and the headline findings (grouped by
   gate, tagged with their action tier — FIX / DEFER / NOTE) into this report as
   a summary. Do not duplicate its entire contents; link to its file instead.
   - Whatever the review concludes (pass, fail, or DEFER/NOTE-only findings),
     this is the last stage that can change the overall pass/fail status — the
     review's verdict (FAILED iff any FIX findings) is reflected in the overall
     pre-flight status. DEFER and NOTE findings do not flip the status.

6. **Stage 4 — Identify mutation-testing targets (advisory; never run them).**
   Mutation testing (`stryker-run.ps1`, runner = `Plantry.Tests.Unit`) is *not*
   part of the gate and is far too slow to run here — this stage only works out
   **which projects the user should run afterward**, given what changed. It is
   advisory and never changes the overall status. Procedure:
   - Get the changed source files for the branch: `git diff --name-only
     main...HEAD -- 'src/**'` (ignore `tests/`, migrations, and non-code).
   - Read the project list Stryker actually mutates from `stryker-run.ps1` (the
     `$projects` array) — that, not the whole solution, is the mutation-tested
     set. Map each changed `src/` file to its owning project.
   - **Recommend** a project when it is *both* in the `$projects` array *and*
     has changed **mutatable logic** — method bodies, branches, guards,
     operators. Exclude changes that have nothing for Stryker to mutate: pure
     interfaces, a class declaration / `: IFoo` addition with no body change,
     DTO/record field additions, EF migrations, DI registration, config.
   - **Flag a coverage gap** when a changed project now has unit-test coverage
     (e.g. via a new `InternalsVisibleTo` into `Plantry.Tests.Unit`, or a new
     test class targeting it) but is **absent from the `$projects` array** —
     recommend adding it to `stryker-run.ps1`, because it is currently getting
     zero mutation coverage.
   - **Note what is out of scope**: projects with no unit-test project at all
     (notably `Plantry.Web` and the `*.Infrastructure` projects not in the
     array) are not mutation-tested — Stryker is wired only against
     `Plantry.Tests.Unit`, so Integration/E2E-only code is covered by those
     suites instead, not by mutation.
   - Give the concrete `dotnet stryker --project <Name>` command per recommended
     project. List the projects you considered and **deliberately skipped**,
     with the one-line reason, so the omission is visible rather than silent.
   - If any earlier stage failed (early stop), mark this stage **SKIPPED** like
     the others — there is no point identifying mutation targets on a red run.

7. Write the consolidated report (see *Report format*) to the path determined
   in step 1, then tell the user the overall result and the report path. If any
   stage failed, say which one and why, in one or two sentences — don't restate
   the whole report inline.

## Report on early stop

If any gating stage fails, the report still gets written — it just omits the
stages that didn't run (mark them **SKIPPED** with a one-line reason, e.g. "not
run — build failed"). Never silently drop a stage; always account for **every**
stage — Build, Test, Test JS, Coverage, Conventions, Review, and Mutation — in
the report, even when incomplete. (Stage 4 mutation is advisory and never flips
the overall status, but it is still marked SKIPPED on an early stop like the
rest.)

## Report format

```markdown
# Pre-flight — {branch} — {timestamp}

**Overall: PASS | FAILED — build | FAILED — tests | FAILED — tests (JS) | FAILED — coverage | FAILED — conventions | FAILED — review**

## 1. Build
Status: PASS | FAIL
- Analyzer/compiler warnings (n): file:line — code — message (one per line; "none" if empty)
- If FAILED: full error output

## 2. Tests
Status: PASS | FAIL | SKIPPED (not run — build failed)
- Per project (dotnet): pass/fail/skipped counts
- Island JS (`node --test`): tests N / pass N / fail N
- Failing tests (if any): test name, project, failure message/assertion, stack trace excerpt

### 2c. Coverage
Status: PASS | FAIL | SKIPPED (not run — {build|tests} failed)
- Engine: ReportGenerator (pinned local tool) over .preflight/coverage/**/coverage.cobertura.xml
- Table: assembly | tier (domain/infra/advisory) | line % | floor | pass/fail
- Any floors raised this run (ratchet): assembly old→new, or "none"

## 2.5 Testing conventions
Status: PASS | FAIL | SKIPPED (not run — {build|tests|coverage} failed)
- Naming/placement: PASS/FAIL (list any offending project)
- Required framework refs (xunit + Microsoft.NET.Test.Sdk + coverlet.collector): PASS/FAIL (list any project missing a ref)
- Un-reasoned skipped tests: PASS/FAIL (list each `[Fact(Skip)]`/`[Theory(Skip)]`/`[Ignore]` without a tracked reason)
- Advisory smells (no-assert tests, etc.): list, never fails

## 3. Code review
Status: PASS | FAIL | SKIPPED (not run — {build|tests|coverage|conventions} failed)
- Full report: ./reviews/{timestamp}-{branch}.md
- Verdict: {the review's own pass/fail judgement — FAILED iff any FIX findings}
- Headline findings by gate, tagged FIX / DEFER / NOTE, summarized — not the full report
- DEFER follow-ups: {bead IDs filed for DEFER findings, if any}

## 4. Recommended mutation testing
Status: ADVISORY | SKIPPED (not run — earlier stage failed)
- Not part of the gate; identifies which projects to run afterward based on this diff.
- Recommended (table): project | reason (the changed mutatable logic) | `dotnet stryker --project <Name>`
- Coverage gap (if any): changed + now unit-tested but missing from `stryker-run.ps1` → add it
- Skipped/not-covered: projects considered and excluded, each with a one-line reason
```

## Coverage floor table (ratchet)

Tier targets: **domain 85%**, **infra 60%**. Advisory assemblies
(`Plantry.Web`, `Plantry.AppHost`, `Plantry.ServiceDefaults`,
`Plantry.Migrator`) have no floor — reported, never gated.

The floor is the pass bar for a gated assembly. It is a **ratchet**: it starts at
`min(tier target, measured line %)` (rounded down to a whole %), only ever rises,
and is capped at the tier target. When a run measures a whole-% coverage above an
assembly's floor, raise the floor to that value (Stage 2c step 4). Once a floor
reaches its tier target it is just the target and is no longer listed as an
exception.

**Assemblies not listed below are held at their full tier target** (85 domain /
60 infra). New assemblies get no grandfathering — they must meet the full tier
target from the first run. The list below is only the grandfathered exceptions
that existed when the gate landed and were below target.

Baseline measured 2026-07-04 (full solution: unit + integration + E2E + web +
architecture). Grandfathered exceptions (floor < tier target):

| Assembly | Tier | Measured | Floor |
|---|---|---|---|
| Plantry.Identity | domain | 30.6% | 30 |
| Plantry.Migration.Grocy | domain | 74.1% | 74 |
| Plantry.Deals | domain | 76.6% | 76 |
| Plantry.Catalog | domain | 84.7% | 84 |
| Plantry.Ai.Infrastructure | infra | 40.0% | 40 |

All other gated assemblies met their tier target at baseline and are held at it:
domain (target 85) — Intake, Inventory, MealPlanning, Pricing, Recipes,
SharedKernel, Shopping; infra (target 60) — Catalog, Deals, Identity, Intake,
Inventory, MealPlanning, Pricing, Recipes, Shopping (all `*.Infrastructure`).

## Notes

- Run stages strictly sequentially — never start a stage before the previous
  gating stage is green. The whole point is that a red earlier stage makes the
  later ones meaningless noise. (Stage 4 mutation is advisory but still runs
  last, and is SKIPPED on any early stop.)
- "All analyzer warnings" means everything the compiler/analyzers emitted
  during the build that succeeded — not just the ones in changed files. If the
  list is long, still include all of it; that's the point of the report living
  on disk rather than in chat.
- The `.preflight/` directory is local scratch output (git-ignored) — the raw
  coverage files, the ReportGenerator output, and the report all live there. The
  one thing that is **not** scratch is `.config/dotnet-tools.json` (the pinned
  ReportGenerator manifest); it is committed so every run uses the same engine
  version the release gate does.
