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
- **DEFER** — name the boundary trigger (contested-decision / out-of-scope / missing-test-infra
  / low-confidence), then **verify the recommendation against the tree it would land in** (see
  "Verifying a DEFER recommendation" below) before writing it as bead-ready text.
- **NOTE** — informational; no action.

End with an overall verdict: **PASS** (no FIX findings) or **FAILED** (one or more FIX
findings), and one sentence explaining the call. DEFER and NOTE findings do not affect the
verdict.

Write the report to disk at `.reviews/<timestamp>-<branch>.md`.

## Verifying a DEFER recommendation

The critic sees only the diff under review — but a DEFER's *recommendation* describes work in
code the diff never touched. Filed verbatim, an unverified recommendation is a guess dressed up
as a spec: it gets filed as a bead and worked as though it were scoped, and every one of the
four beads below shipped a recommendation that pointed the opposite direction from its own
(correct) finding once someone looked one level out of the diff.

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
   one side of it. A trade the codebase has already made is a fact to cite (see the FIX/DEFER
   boundary's "resolve apparent design forks against the codebase first" in
   `.claude/review-criteria.md`), not a fork left open to defer.

**Escape hatch — `recommendation-unverified`.** When a check above applies but can't be run
within the review's time budget, don't skip it silently — tag the finding
`recommendation-unverified` in its DEFER line. This changes what the filed bead *is*: a bead
tagged `recommendation-unverified` is a **lead** ("this is worth someone looking at"), not a
**spec** ("here is the fix"). Carry the tag into the bead as a label so whoever eventually works
it knows to re-derive the recommendation from the tree rather than implement it verbatim.

### Why this lives in the critic's prompt, not a separate preflight/orchestrator step

Tree verification is a property of the *recommendation*, not of the filing mechanics — a wrong
recommendation is wrong the moment it's written, regardless of what downstream process later
turns it into a bead. Checking it here, inside the review pass that already has the diff and the
tree open, catches the error at its source, with the least duplicated context. A separate
verification step inserted between review and `bd create` (in `plantry-preflight` /
`pipeline-orchestrator`) would run after the critic had already committed to a wrong finding, and
would need to reconstruct — from scratch, in a different process — context this review pass
already has for free. The added cost (tree-reading in a pass that's deliberately diff-scoped) is
bounded: it only fires per-DEFER, and only for the finding shapes the five checks above name, not
for every finding.

Sanity-checked against the four cases that motivated this requirement: for `plantry-bc2c`,
checks 1 and 2 would have caught both the phantom project reference and the guard's 44
unmatched-token failure on arrival; for `plantry-7p32`, check 3 would have caught the inverted
rename; for `plantry-kfjj`, check 5 would have caught the "unsettled fork" that
`docker-compose.prod.yml` already resolves; for `plantry-sl2e`, check 4 would have caught the
ungrounded factory count, and check 5 would have caught the extension-method recommendation
contradicting the house convention already in `Infrastructure/`.
