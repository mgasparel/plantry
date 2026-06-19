# Agentic engineering workflow

> **How** Plantry's fully-agentic engineering works in practice. The principle and
> its rationale are [ADR-018](../ADRs/ADR-018.md); the CI gate it relies on is
> [ADR-016](../ADRs/ADR-016.md). The agent-facing source of truth for each step is the
> skill/agent definition under `.claude/skills/` and `.claude/agents/`; this doc is the
> human-facing map over them.
>
> **⚠️ STATUS: STUB.** This is a skeleton to be fleshed out. The new (PR-gated) process
> needs a deep-dive before this is authoritative — tracked under the Phase-4 epic. Sections
> marked _TODO_ are deliberately thin.

## The principle (in one line)

Agents carry out the implementation work humans plan; humans own intent and step in
pragmatically; the process is non-dogmatic and mutable. See [ADR-018](../ADRs/ADR-018.md).

## The loop

```
bd ready  ──▶  claim  ──▶  implement-ticket-worker (in a worktree)
                              └─ pre-flight gate: build → test (incl. E2E) → Opus critic (≤3 passes)
                                    └─ commit  ──▶  [TARGET] push branch → PR → CI → merge-on-green
```

_TODO (deep-dive): the authoritative end-to-end description of the **target** loop — push
+ PR + merge-queue gating — once Phase 4 lands. Today's loop still merges locally; see
"In-flight changes" below for the gap._

## Roles — human vs agent

| | Owns |
|---|---|
| Human | Product/architecture decisions, ADRs, issue specifications, review/approval; pragmatic intervention when judgement is needed. |
| Agent pipeline | Turning planned issues into implemented, gated, merged code — by default. |

_TODO (deep-dive): the break-glass cases — when and how a human takes over, and how that
change still goes through the gate (ADR-018: the gate is author-agnostic)._

## The skills

| Skill / agent | Role | Status |
|---|---|---|
| `implement-ticket-worker` | Implements one claimed issue end-to-end behind the pre-flight gate | **Changing** (Phase 4: push + PR) |
| `pipeline-orchestrator` | Parallel claim/dispatch/merge loop | **Retiring** — describes an unused parallel model; serial is the chosen model |
| `plantry-preflight` | Build + test + review as a pre-commit gate | Stable |
| `plantry-code-review` | Plantry-specific review criteria | Stable |
| `triage` / `groom` | Backlog ranking / label classification | Stable |
| `dogfood` | File issues from real app use | Stable |

_TODO (deep-dive): per-skill responsibilities, inputs/outputs, and how they compose._

## The pre-flight / critic gate

The quality mechanism that makes "agents do the work" safe: build → full test suite incl.
E2E → an independent Opus critic (≤3 passes), with CI ([ADR-016](../ADRs/ADR-016.md)) as the
authoritative re-run. Author-agnostic by design.

_TODO (deep-dive): the critic contract (gates, FIX/DEFER/NOTE tiers), pass/park outcomes,
and how it relates to CI now that CI is the merge gate._

## Beads as the work ledger

Planned work lives as beads issues; the pipeline claims, implements, and closes them. See
`AGENTS.md` for mechanics.

_TODO: note that `AGENTS.md`'s session-completion rule still mandates a direct `git push`
to `main`, which conflicts with the target branch-protection model — reconciled in Phase 4._

## In-flight changes (Phase 4 — ADR-016 / ADR-018)

The move from "gate locally, merge to main directly" to "gate locally as a pre-filter, then
PR + CI as the authoritative serial merge gate." Tracked as a separate epic:

- `implement-ticket-worker`: push branch + open PR; `PR:` in verdict; `ci-failed` park reason.
- Merge step: `gh pr merge --auto`; `bd close` moves to post-merge.
- Retire/rewrite `pipeline-orchestrator` as a serial driver (or delete).
- Reconcile `AGENTS.md` session-completion with branch protection.
- Enable branch protection + merge queue + auto-merge (depends on CI being green).

_TODO (deep-dive): this whole section is the subject of the deep-dive that produces the
authoritative version of this document._
