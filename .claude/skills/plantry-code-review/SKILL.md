---
name: plantry-code-review
description: >-
  Review Plantry code changes for both standard correctness/quality issues AND
  this project's specific architectural conventions and product values — DDD
  bounded-context discipline, household tenancy, the single consumption
  primitive, AI-as-untrusted-input staging, hypermedia-only UI (no SPA/Node),
  and persistence conventions. USE FOR: reviewing a diff, PR, or recently
  written/edited code in src or tests; "review this", "code review", "does this
  follow our conventions", pre-commit/pre-PR self-checks.
  DO NOT USE FOR: generic reviews of code outside this repo.
license: MIT
metadata:
  author: plantry
  version: "0.4.0"
---

# Plantry code review

Plantry is a DDD modular monolith with a deliberately narrow architecture: nine
bounded contexts in one process/database, hypermedia UI (no SPA, no Node), AI treated
as untrusted input, and one primitive for every stock removal. Code that's clean but
violates one of these decisions is a regression even if it compiles and passes tests —
that's the kind of drift this review exists to catch.

## Criteria

Read `.claude/review-criteria.md` for the full gate definitions (all gates) **and the
Action tiers section** before reviewing. Apply all gates, and classify every finding into
exactly one action tier — **FIX**, **DEFER**, or **NOTE** — using the FIX-vs-DEFER boundary
in that document (effort/size is never a DEFER reason; an apparent design fork an existing
ADR or pattern already settles is a FIX).

## Output format

Group findings by gate. For each finding: **file:line**, what's wrong and why it
matters in *this* codebase (name the rule — e.g. "bypasses the single consumption
primitive", "leaks across the bounded-context boundary" — not just "this looks off"),
and a concrete fix, ideally pointing at an existing pattern to mirror.

Tag each finding with its tier:
- **FIX** — must be resolved before merge; include explicit, self-contained fix instructions.
- **DEFER** — name the boundary trigger (contested-decision / out-of-scope / low-confidence;
  see the trigger definitions in `.claude/review-criteria.md` — "missing test infrastructure"
  is not a trigger: test-layer infra is built in-case as part of a FIX), then **verify the
  recommendation against the tree it would land in** (see "Verifying a DEFER recommendation"
  below) before writing it as bead-ready text.
- **NOTE** — informational; no action.

End with an overall verdict: **PASS** (no FIX findings) or **FAILED** (one or more FIX
findings), and one sentence explaining the call. DEFER and NOTE findings do not affect the
verdict.

Write the report to disk at `.reviews/<timestamp>-<branch>.md`.

## Verifying a DEFER recommendation

Before writing a DEFER's recommendation as bead-ready text, run the tree-verification checks
in `.claude/review-criteria.md` under "Verifying a DEFER recommendation" — this is the
canonical definition, shared with `plantry-preflight` Stage 3 and the autonomous pipeline's
inline critic, so it lives in one place rather than being copied per reviewer. It covers: the
five checks to run against the current tree (does the named file/project exist and fit, does a
proposed guard pass today, is a rename targeting the right side, is a cited count grounded, is
a "design fork" already resolved by the codebase), and the `recommendation-unverified` escape
hatch for when a check applies but can't be run in the time budget.
