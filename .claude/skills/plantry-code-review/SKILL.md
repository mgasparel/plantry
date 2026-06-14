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
  version: "0.3.0"
---

# Plantry code review

Plantry is a DDD modular monolith with a deliberately narrow architecture: nine
bounded contexts in one process/database, hypermedia UI (no SPA, no Node), AI treated
as untrusted input, and one primitive for every stock removal. Code that's clean but
violates one of these decisions is a regression even if it compiles and passes tests —
that's the kind of drift this review exists to catch.

## Criteria

Read `.claude/review-criteria.md` for the full gate definitions (Gates 1–8) **and the
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
- **DEFER** — name the boundary trigger (contested-decision / out-of-scope / missing-test-infra
  / low-confidence) and give an actionable recommendation (this is bead-ready text).
- **NOTE** — informational; no action.

End with an overall verdict: **PASS** (no FIX findings) or **FAILED** (one or more FIX
findings), and one sentence explaining the call. DEFER and NOTE findings do not affect the
verdict.

Write the report to disk at `.reviews/<timestamp>-<branch>.md`.
