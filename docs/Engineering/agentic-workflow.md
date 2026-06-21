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
driver (serial, one epic in flight)
  │  input: a single bead | an epic | a set of beads
  ▼
claim bead ──▶ route to an epic integration branch  epic/<epic-id>
  │             (curated epic, or the rolling rollup for loose one-offs)
  ▼
implement-ticket-worker  (worktree, branch issue/<id> cut off epic/<epic-id>)
  │  LOCAL pre-flight gate: build → test (incl. E2E) → Opus critic (≤3 passes)
  │    ├─ PASS  → single commit → driver merges issue/<id> into epic/<epic-id> (no per-child PR/CI), labels it `staged`
  │    └─ can't pass → PARK  (a parked child blocks its epic from shipping)
  ▼
epic 100% staged?  ── no ──▶ drain: claim next child of this epic
  │ yes
  ▼
rebase epic onto origin/main ──▶ ONE epic→main PR ──▶ CI (fast gate — NO AI)
                                   └─ green ──▶ auto-merge to main (no queue)
                                                  └─▶ batch-close all children + epic, cleanup
                                                         └─▶ driver advances
```

> Full suite (integration + E2E + coverage) and deploy run at the **release tag**, not on
> this PR — see [ADR-016](../ADRs/ADR-016.md) (amended) and `plantry-49hm`. Batching one PR
> per epic instead of per child is `plantry-ekoo`.

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
- **One PR per epic (v5, `plantry-ekoo`).** Every issue is routed to an **epic integration
  branch** — its curated epic, or the rolling **rollup** epic the driver auto-attaches loose
  one-offs to. The worker branches off `epic/<epic-id>`; on `PASS` the driver merges the
  child into the epic branch (no per-child PR, no per-child CI) and labels it `staged`. When
  the epic is **100% staged**, the driver rebases it onto fresh `origin/main`, opens **one**
  `epic→main` PR, and on green CI batch-closes every child + the epic. A 10-child epic costs
  one PR, not ten.
- **Epics ship whole.** A flush happens only at 100%; a parked/failed child blocks its whole
  epic (uniformly — curated or rollup) until a human clears it. Nothing partial reaches `main`.
  Rollups accept one-offs until **sealed** (a `sealed` label, or `ROLLUP_MAX_CHILDREN`
  children), then ship like any fixed-set epic.
- **One epic in flight.** The driver drains the active epic (ships or parks) before starting
  another, so the epic branch never falls behind `main` under serial work. A merge queue would
  be the textbook concession to the concurrent-manual-push case, but it is unavailable on this
  personal-account repo (deferred until org-owned; ADR-016). Until then the **rebase-before-PR**
  step plus the pre-merge mergeability guard cover it: a behind/conflicting epic PR is parked
  rather than landed stale.

## The worker (`implement-ticket-worker`)

Implements one claimed bead end-to-end in an isolated worktree, then gates locally:

- Read the bead; interpret minor ambiguity (document via `bd comment`), park on significant
  underspecification.
- Implement in `../worktrees/<id>/` on branch `issue/<id>`, **cut off the epic branch**
  `epic/<parent-id>` (not `main`) so it builds on already-staged siblings.
- **Local pre-flight gate** (see below). All fix cycles fold into one commit.
- On PASS: single commit, then **hand the branch back** — no push, no PR. The driver merges
  it into the epic branch and opens the single epic PR later. (A direct, no-epic invocation by
  a human is the one exception: the worker pushes + opens its own PR to `main`.)
- Park reasons: `build-loop-exhausted`, `test-loop-exhausted`, `critic-loop-exhausted`,
  `underspecified-scope`, `blocked-on-dependency`, `unrecoverable-error:<detail>`. (CI failures
  now surface on the *epic* PR and are the driver's reconcile concern, not the worker's.)

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

- Merge the **epic** PR via `gh pr merge <pr> --auto --merge`; GitHub merges when the `fast`
  check + branch protection are satisfied. No merge queue (unavailable on a user-owned repo);
  the driver rebases the epic onto fresh `origin/main` before opening the PR and parks a
  behind/conflicting PR rather than landing it stale.
- **`bd close` and cleanup move to _after_ the epic PR merges** — all children + the epic are
  batch-closed at once; never close a bead for a PR that later fails CI.
- An epic PR whose CI fails after every child's local PASS triggers the **auto-fix reconcile**
  (below); only after it exhausts does the driver park the *epic* `ci-failed`.

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

The PR-gated serial workflow is **live** (epic `plantry-hifn`, closed 2026-06-20). Two
later changes reshaped it:

- **`plantry-49hm`** — the slow suite (integration + E2E + coverage) and deploy moved to the
  **release tag**; per-PR CI is now a `fast` gate only (build + unit + architecture). The
  `main` ruleset's required check is `fast` (the `e2e` requirement was dropped). Full detail:
  [ADR-016](../ADRs/ADR-016.md) (amended).
- **`plantry-ekoo`** — the driver batches **one PR per epic** rather than per issue: children
  merge into an `epic/<id>` integration branch and ship together. Loose one-offs roll into a
  `rollup` epic. See "The driver" above.

The one deviation from the original ADR-016 target remains the **merge queue**, unavailable on
a personal-account repo and deferred until org-owned; the rebase-before-PR step + the driver's
mergeability guard cover the concurrent-manual-push case meanwhile.
