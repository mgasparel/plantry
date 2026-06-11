---
name: mutation-triage
description: >-
  Triage Stryker mutation-testing survivors using the four-bucket system:
  missing scenario → spec-readable test or bug bead; equivalent mutant / dead
  code / low-value scope → exclude with justification. Enforces the spec-test
  gate: a test that cannot be stated as a Given/When/Then a product person
  could read must not be written. USE FOR: "triage survivors", "mutation
  triage", "what do I do with these surviving mutants", deciding what tests to
  write from mutation output; also auditing EXISTING tests for implementation
  coupling ("audit my mutation tests"). DO NOT USE FOR: just viewing the
  report/score (use stryker-report), running Stryker (use stryker-run.ps1).
license: MIT
metadata:
  author: plantry
  version: "0.3.0"
---

# Mutation triage

Mutation testing is a diagnostic, not a goal. A surviving mutant means "this
change went undetected" — never "add an assertion." Find the missing
*scenario*, or establish there isn't one and exclude the mutant.

## The spec-test gate

Before writing any test to kill a mutant: **can it be stated as a
Given/When/Then a product person could read?**

- Yes → write it; it has value independent of mutation score.
- No → it would be implementation-coupled. Exclude the mutant instead.

Never write a test whose only purpose is raising the score.

## Triage procedure

Triage ALL survivors first and present the summary (see Output format), then
implement the actions. Do not interleave classification and edits.

### 1. Get the survivor list

```powershell
python .claude/skills/stryker-report/parse-report.py
```

The script auto-finds the newest report and groups survivors by file. If it
errors, there is no report — tell the user to run `.\stryker-run.ps1`; do not
run Stryker yourself.

### 2. Classify each survivor

Read the source around the mutated line, then pick one bucket:

| Bucket | When | Action |
|---|---|---|
| **Missing scenario** | A real business case would exercise this and isn't tested | Step 3 |
| **Equivalent mutant** | Code changes but no observable behavior does | Exclude (step 4) |
| **Dead code** | Branch unreachable under valid inputs | Trivial → delete; questions the design → task bead |
| **Low-value scope** | Infrastructure/projection/mapping; gap is not a domain-rule risk | Exclude file/namespace in `stryker-config.json` |

Heuristic: aggregates and value objects in `*/Domain/` are high-value — guard
clauses, status transitions, operator boundaries (`>=` vs `>`), null/empty
checks there are almost always missing scenarios. `*/Application/` projection
and infrastructure code is usually low-value scope.

### 3. Missing scenario → test or bug bead

First decide: **is the production behavior for this scenario actually correct?**

**Correct, test missing** → write the test (spec-test gate applies):
- Name reads as a sentence: `[State]_[Operation]_[Outcome]`.
- Assert observable outcomes only: returned error codes, raised events,
  contractual state. Error codes are contract; the branch that produced them
  is not. Error messages are presentation — never assert their text.
- One meaningful assertion (or a small cluster for one concept), not a pin of
  every property touched by the operation.

**Wrong or suspect** → do NOT fix it now. File a bug bead and continue triage:

```bash
bd create --type=bug --priority=<1 if data-integrity/user-facing, else 2> \
  --title="Mutation-find: <one-line behavior description>" \
  --description="<file:line — surviving mutation — why current behavior is wrong — expected behavior>"
```

The `Mutation-find:` prefix keeps a queryable record (`bd search Mutation-find`)
of real bugs caught by mutation testing.

### 4. Exclude a non-scenario mutant

Prefer config-level exclusions — they keep production code clean and
centralize reasoning.

**Tier 1 — file/path exclusion** (`stryker-config.json` `mutate` array):
For the Low-value scope bucket. Extend the existing patterns:
```json
"mutate": ["src/**", "!src/**/Migrations/**", "!src/**/*DbContext.cs", "!src/**/SomeFile.cs"]
```

**Tier 2 — mutator-type exclusion** (`stryker-config.json` `ignore-mutations`):
When the same mutator type produces equivalent survivors *systematically*
across the codebase. Document the reasoning in the PR — `stryker-config.json`
has no comment syntax:
```json
"ignore-mutations": ["string"]
```

**Tier 3 — inline comment (last resort)**:
Only for a genuine one-off equivalent in domain code that doesn't fit a file
or type pattern:
```csharp
// Stryker disable once <MutatorName>: equivalent — <reason why this specific mutation has no observable effect>
```

## Audit mode — existing tests written while chasing score

Use when cleaning up implementation-coupled tests, not triaging new survivors.

### Smells specific to this codebase (from git history)

These patterns were found in commits explicitly written to "fill mutation gaps":

1. **Property-pinning constructors** — tests named `X_Sets_Y_And_Z` or
   `Start_Sets_Composite_Id_And_Properties` that assert 3+ constructor-assigned
   properties. Ask: if the internal representation changed but observable
   behavior stayed the same, would this still pass? If yes → pins
   implementation.

2. **Error-message text pinning** — `Assert.Contains("3-letter ISO 4217", ex.Message)`
   or `Assert.Contains("cannot be negative", ex.Message)`. Error *codes* are
   contract; error *messages* are presentation. Pin the code, not the wording.

3. **Tautological equality** — `Assert.True(a == b)` where both objects are
   trivially identical fresh constructs, or bare `GetHashCode` comparisons.
   If equality is used for deduplication or set membership, test *that*
   behavior instead.

4. **Coverage-shape / gap-fill tests** — commits titled "fill gaps" or "cover
   missed mutants" that assert display fallback values like `"Multiple"`,
   `"Unknown product"`, or `"?"`. These verify a string constant survived a
   code path, not a product rule. Apply the spec-test gate: if you cannot
   describe what breaks for a user when this assertion fails, delete the test.

### Decision rule

For each identified test, apply the spec-test gate:
- **Passes** → rewrite: keep the scenario intent, remove implementation-coupled
  assertions, replace with outcome-focused ones.
- **Fails** → delete, then re-run `dotnet stryker --project <Name>` for the
  affected project. Triage anything that newly survives via step 2. Never
  delete without this check.

Fix on touch, not big-bang: when editing a source file, sweep its test file in
the same pass. Goal: same or better score with fewer, better tests.

## Output format

Per survivor (or smelly test), grouped by source file:

```
[FILE] src/Plantry.X/Domain/Y.cs : line N
Mutation: <what Stryker changed>
Bucket: <Missing scenario | Equivalent | Dead code | Low-value scope>
Action: <Write test "..." | File bug bead "Mutation-find: ..." | Exclude via stryker-config.json [tier] | Delete dead code>
Reason: <why this classification>
```

Include full proposed test bodies and exact config changes so they can be
applied directly. End with a count-per-bucket summary table and a one-line
projected score after the changes.
