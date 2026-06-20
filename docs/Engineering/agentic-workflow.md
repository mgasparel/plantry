# Agentic engineering workflow

> **How** Plantry's fully-agentic engineering works. The principle and rationale are
> [ADR-018](../ADRs/ADR-018.md); the CI gate it relies on is [ADR-016](../ADRs/ADR-016.md).
> The **agent-facing source of truth** for each step is its skill/agent definition under
> `.claude/skills/` and `.claude/agents/`; this doc is the human-facing map over them.
>
> **Scope:** this describes the PR-gated serial workflow, which is **live** (epic
> `plantry-hifn`, closed 2026-06-20). The one unrealized piece of the ADR-016 target is the
> merge queue — unavailable on a personal-account repo, deferred until the repo is org-owned.

## Principle

Agents carry out the implementation work humans plan; humans own intent and step in
pragmatically; the process is non-dogmatic and mutable ([ADR-018](../ADRs/ADR-018.md)).

## Hard constraint: AI runs locally only

Plantry is built on a **Claude Code subscription, not an API plan.** Every AI step —
crucially the Opus critic — runs **locally through the Claude Code CLI**. Consequences that
shape the whole workflow:

- **CI never runs AI.** CI is build + test + E2E + coverage only. The review/critic gate
  cannot live in CI and does not.
- **The review gate is local and process-enforced**, not CI-enforced (see "The two gates").
- **GitHub is driven from the local CLI** (`gh`) — opening PRs, enabling auto-merge — not
  from server-side automation that would need API credentials.

## The loop

```
driver (serial, one in flight)
  │  input: a single bead | an epic | a set of beads
  ▼
claim bead ──▶ implement-ticket-worker  (isolated worktree, branch issue/<id>)
                 │  LOCAL pre-flight gate:
                 │    build → test (incl. E2E) → Opus critic (≤3 passes)
                 │      ├─ PASS  → single commit → push issue/<id> → gh pr create
                 │      └─ can't pass (rounds exhausted / underspecified / blocked) → PARK
                 ▼
              PR ──▶ CI (build · test · E2E · coverage — NO AI)
                       └─ green ──▶ auto-merge to main (no queue — see note)
                                       └─▶ bd close + worktree/branch cleanup
                                              └─▶ driver advances to next bead
```

## The two gates

Quality is split across two gates because of the local-AI constraint:

| Gate | Runs | Enforces | How enforced |
|---|---|---|---|
| **Review gate** (Opus critic) | Local, in the worker | Code quality, convention adherence, scope delivery (the Plantry review criteria) | **Process**: the worker only pushes + opens a PR if the critic passed. Never in CI. |
| **Correctness gate** (CI) | GitHub Actions | Build + full test suite incl. E2E + coverage | **Branch protection**: a PR cannot merge until the CI check is green. Author-agnostic. |

For **agent** PRs the review gate is already satisfied before the PR exists (the worker
gates on it). For **human** PRs (break-glass, below) the correctness gate still applies via
CI; the review gate is the human's judgment — they may run the critic locally
(`/code-review`) but nothing forces it. This is the practical shape of ADR-018's
"author-agnostic gate": CI correctness is enforced for everyone; AI review is enforced by
process for agents and by judgment for humans, because it cannot run in CI.

## The driver

A thin **serial** loop — the `pipeline-orchestrator` skill, rewritten from its earlier
parallel form into a serial driver (ADR-016). It accepts flexible input and processes
strictly one issue at a time through merge:

- **Input:** a single bead ID, an epic ID (work its ready children), or an explicit set of
  bead IDs.
- **Per issue:** claim → dispatch the worker → on `PASS`, enable auto-merge and wait. CI
  green → the PR merges → `bd close` + cleanup → next. CI red → run the **auto-fix reconcile**
  (below) before giving up. On a parked verdict, log and move on (human picks it up).
- **One in flight.** No parallel dispatch, no merge-race logic — serialization is inherent.
  A merge queue would be the textbook concession to the rare concurrent-manual-push case,
  but it is unavailable on this personal-account repo (deferred until the repo is org-owned;
  ADR-016). Until then the driver's pre-merge mergeability guard covers that case: it parks a
  PR that has gone behind/conflicting rather than letting a stale-but-green branch land.

## The worker (`implement-ticket-worker`)

Implements one claimed bead end-to-end in an isolated worktree, then gates locally:

- Read the bead; interpret minor ambiguity (document via `bd comment`), park on significant
  underspecification.
- Implement in `../worktrees/<id>/` on branch `issue/<id>`.
- **Local pre-flight gate** (see below). All fix cycles fold into one commit.
- On PASS: single commit → **`git push -u origin issue/<id>` → `gh pr create`** →
  return a verdict including **`PR: <number>`**.
- Park reasons: `build-loop-exhausted`, `test-loop-exhausted`, `critic-loop-exhausted`,
  `underspecified-scope`, `blocked-on-dependency`, `unrecoverable-error:<detail>`, and
  **`ci-failed`** (local pre-flight passed but CI went red on the PR).

## The pre-flight / critic gate (local)

The worker's quality loop, all local:

1. **Build** — fix-and-retry up to 3; else park `build-loop-exhausted`.
2. **Test** — whole solution incl. E2E (boots Aspire + Docker). A suite an acceptance
   criterion depends on that did not execute is a hard stop, not a footnote. Fix-and-retry
   up to 3; else park `test-loop-exhausted`.
3. **Opus critic** — a fresh Opus sub-agent reviews the diff against `.claude/review-criteria.md`
   (Gates 1–8) and returns FIX / DEFER / NOTE findings. Any FIX → fix and re-loop (≤3 passes);
   3 passes still failing → park `critic-loop-exhausted`. PASS → file DEFER beads, then proceed.

Every pass writes a `.preflight/` report and a `bd comment` audit line.

## Break-glass: human intervention

Triggered by a **park** — the automation cannot confidently merge (rounds exhausted, scope
underspecified, unrecoverable error). It is *not* arbitrary hand-editing. The worker leaves
the worktree and branch in place; the bead is `blocked` + `needs-human` with the outstanding
findings in a comment. A human then:

- resolves on the preserved branch (using their own judgment, and the local critic if they
  want it), then takes it through the **same CI correctness gate** via a PR; or
- re-specs/closes the bead if the park was about scope.

The gate doesn't care that a human authored the fix — CI still must be green to merge.

## Beads as the work ledger

Planned work lives as beads; the driver/worker claim, implement, and close issues; mechanics
are in `AGENTS.md`, whose session-completion rule follows the PR flow: nothing is pushed
directly to `main`; a session is complete only once the feature branch is pushed and the PR
is open (or merged).

## Merge & cleanup mechanics

- Merge via `gh pr merge <pr> --auto` (squash); GitHub merges when CI + branch protection
  are satisfied. There is no merge queue (unavailable on a user-owned repo); the driver's
  mergeability guard handles the concurrent-manual-push case by parking behind/conflicting PRs.
- **`bd close` and worktree/branch cleanup move to _after_ the PR merges** — never close a
  bead for a PR that later fails CI.
- A PR whose CI fails after a local PASS triggers the **auto-fix reconcile** (below); only
  after it exhausts does the driver park the issue `ci-failed`.

## Auto-fix on red CI

A CI failure *after* a local PASS is by definition something local didn't catch — most often
a **Windows-local vs Linux-CI** gap (case-sensitive imports, line endings, an un-committed
file) or a flaky E2E boot. Because AI is local-only (no API for CI), the fix loop runs
**locally, driven by the driver via `gh`** — CI cannot fix itself:

1. **Detect.** The driver polls `gh pr checks <pr>`. Green → merge proceeds. Red → step 2.
2. **Classify** from `gh run view <run-id> --log-failed`:
   - **Flaky / transient** (intermittent E2E boot, runner/network) → `gh run rerun --failed`
     once, then back to waiting. Rerun-before-fix avoids "fixing" nondeterminism.
   - **Env / config** (missing secret, runner image, protection misconfig) → **park**
     `ci-failed`; not a code fix.
   - **Code / test** (compiles on Windows but fails on Linux; missing committed file; a real
     test failure) → step 3.
3. **Fix.** Dispatch the local **`ci-fix-worker`** agent at the worker's preserved worktree,
   handed the failing logs. It reproduces if it can (often it can't — the failure is
   env-specific), patches on `issue/<id>` reasoning from the logs, re-runs local build+test to
   confirm, and pushes. CI re-runs → back to step 1.
4. **Bound.** After N fix attempts still red → park `ci-failed` for a human, branch/worktree
   preserved.

`ci-fix-worker` is a **separate agent** from `implement-ticket-worker` (single
responsibility; reusable for human-opened PRs) but reuses the same local pre-flight to
re-validate. Scope discipline applies: a CI fix stays within the PR's footprint — if the red
reveals something deeper, it parks rather than ballooning the diff.

## Status

The PR-gated serial workflow described above is **live** (epic `plantry-hifn`, closed
2026-06-20): the worker pushes `issue/<id>` and opens a PR, branch protection on `main`
(a repository ruleset requiring the `fast` + `e2e` CI checks and a PR) gates the merge, the
driver enables auto-merge and runs the CI reconcile loop, and `bd close` + cleanup happen
post-merge. The one deviation from the original ADR-016 target is the **merge queue**, which
is unavailable on a personal-account repo and is deferred until the repo is org-owned; the
driver's mergeability guard covers the concurrent-manual-push case in the meantime.
