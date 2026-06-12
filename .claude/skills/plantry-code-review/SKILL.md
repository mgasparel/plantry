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

Read `.claude/review-criteria.md` for the full gate definitions (Gates 1–8) before
reviewing. Apply all gates. The blocking/advisory classification for each gate is
specified in that document.

## Output format

Group findings by gate. For each finding: **file:line**, what's wrong and why it
matters in *this* codebase (name the rule — e.g. "bypasses the single consumption
primitive", "leaks across the bounded-context boundary" — not just "this looks off"),
and a concrete fix, ideally pointing at an existing pattern to mirror.

Call out blocking findings explicitly as **BLOCKING**. Call out advisory findings as
**ADVISORY** with a note that they don't prevent PASS.

End with an overall verdict: **PASS** or **FAILED**, and one sentence explaining the
call.

Write the report to disk at `.reviews/<timestamp>-<branch>.md`.
