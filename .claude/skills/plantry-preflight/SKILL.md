---
name: plantry-preflight
description: >-
  Run the full pre-flight gate before a commit/PR — build the solution, run all
  test projects, then run the plantry-code-review skill — and write a single
  consolidated report to ./.preflight/{timestamp}-{branch}.md. Stops at the
  first failing stage; later stages do not run if an earlier one fails. USE
  FOR: "run preflight", "am I ready to commit/push", "pre-PR check", "build and
  test and review this branch". DO NOT USE FOR: running just one of these steps
  in isolation (use `dotnet build`, `dotnet test`, or the plantry-code-review
  skill directly), or reviewing code outside this repo.
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Plantry pre-flight

A single gate that chains three checks in strict order — **build → test → code
review** — and stops at the first one that fails. Each stage's output feeds the
same report; a failing stage is the last thing written to it. This mirrors
"would CI reject this" without needing to push.

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
     failure, and **stop — do not run tests or the code review.**
   - If it succeeds, record the warning list and continue.

3. **Stage 2 — Test.** Run `dotnet test Plantry.sln` from `code/` (this covers
   `Plantry.Tests.Unit`, `Plantry.Tests.Integration`, `Plantry.Tests.E2E`, and
   `Plantry.Tests.Architecture`).
   - Capture per-project pass/fail counts and the names of any failing tests
     with their failure messages/stack traces.
   - If any test fails or a test project fails to run: write the report now
     with status **FAILED — tests**, including Stage 1's warning list (build
     passed) and the full test failure detail, and **stop — do not run the
     code review.**
   - If everything passes, record the summary and continue.

4. **Stage 3 — Code review.** Invoke the `plantry-code-review` skill. It writes
   its own report — note that path, and
   pull its overall pass/fail judgement and the headline findings (grouped by
   gate, with severities) into this report as a summary. Do not duplicate its
   entire contents; link to its file instead.
   - Whatever the review concludes (pass, fail, or advisory-only notes), this
     is the last stage that can change the overall pass/fail status — the
     review's verdict is reflected in the overall pre-flight status.

5. **Stage 4 — Identify mutation-testing targets (advisory; never run them).**
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
   - If Stage 1 or Stage 2 failed (early stop), mark this stage **SKIPPED** like
     the others — there is no point identifying mutation targets on a red build.

6. Write the consolidated report (see *Report format*) to the path determined
   in step 1, then tell the user the overall result and the report path. If any
   stage failed, say which one and why, in one or two sentences — don't restate
   the whole report inline.

## Report on early stop

If Stage 1 or Stage 2 fails, the report still gets written — it just omits the
stages that didn't run (mark them **SKIPPED** with a one-line reason, e.g. "not
run — build failed"). Never silently drop a stage; always account for all four
in the report, even when incomplete. (Stage 4 is advisory and never flips the
overall status, but it is still marked SKIPPED on an early stop like the rest.)

## Report format

```markdown
# Pre-flight — {branch} — {timestamp}

**Overall: PASS | FAILED — build | FAILED — tests | FAILED — review**

## 1. Build
Status: PASS | FAIL
- Analyzer/compiler warnings (n): file:line — code — message (one per line; "none" if empty)
- If FAILED: full error output

## 2. Tests
Status: PASS | FAIL | SKIPPED (not run — build failed)
- Per project: pass/fail/skipped counts
- Failing tests (if any): test name, project, failure message/assertion, stack trace excerpt

## 3. Code review
Status: PASS | FAIL | SKIPPED (not run — {build|tests} failed)
- Full report: ./reviews/{timestamp}-{branch}.md
- Verdict: {the review's own pass/fail judgement}
- Headline findings by gate (blocking vs advisory), summarized — not the full report

## 4. Recommended mutation testing
Status: ADVISORY | SKIPPED (not run — {build|tests} failed)
- Not part of the gate; identifies which projects to run afterward based on this diff.
- Recommended (table): project | reason (the changed mutatable logic) | `dotnet stryker --project <Name>`
- Coverage gap (if any): changed + now unit-tested but missing from `stryker-run.ps1` → add it
- Skipped/not-covered: projects considered and excluded, each with a one-line reason
```

## Notes

- Run stages strictly sequentially — never start Stage 2 before Stage 1 is
  green, never start Stage 3 before Stage 2 is green. The whole point is that a
  red earlier stage makes the later ones meaningless noise.
- "All analyzer warnings" means everything the compiler/analyzers emitted
  during the build that succeeded — not just the ones in changed files. If the
  list is long, still include all of it; that's the point of the report living
  on disk rather than in chat.
- The `.preflight/` directory is local scratch output — if it isn't already covered by `.gitignore`, ask the
  user whether it should be before assuming it's fine to leave untracked or
  tracked.
