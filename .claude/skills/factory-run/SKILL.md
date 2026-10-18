---
name: factory-run
description: Run Plantry work through one serial ticket procedure, either for one named issue or continuously from ready Beads work.
license: MIT
metadata:
  author: plantry
  version: "1.0.0"
---

# Factory run

Use this as the delivery entry point. It supports one named ticket and continuous ready-ticket processing through the same procedure. In continuous mode, finish the current ticket through confirmed merge, Beads close, and successful Beads sync before selecting another.

Read AGENTS.md, run bd prime, and inspect Beads state before acting. Resume any unfinished factory state before claiming new work. Factory state is recorded in Beads comments with the marker FACTORY STATE v1 and includes the phase, issue, branch, worktree, current candidate commit, base commit, pull request, gate results, and next action. Append a new state comment when the candidate or phase changes; never treat an older result as evidence for a newer commit.

At startup, find issues already in progress with a FACTORY STATE v1 comment. Resume the single active ticket. If more than one exists, stop and record the conflict instead of starting another. If there is no active ticket, use the named issue or select one ready, non-epic issue from bd ready whose specification is approved (spec:ready). Read the full issue and exclude needs-spec, needs-split, needs-human, parked, or unresolved-decision work. A named ticket must meet the same readiness requirements; if its spec is missing, use factory-spec before claiming it. Epics only organize dependencies. Work ready children directly; never attach a loose issue to an epic or create an epic delivery branch. Claim the issue atomically and record the initial state before dispatch.

Follow the same delivery sequence for both modes:

1. Dispatch the independent implementer using factory-implement.
2. Require a successful full solution build, each required .NET test project, island JavaScript tests, and testing-convention checks on the candidate commit.
3. Use factory-land to push the issue branch and open one pull request after local verification passes. Keep the same PR through repairs; do not create a second PR for the ticket.
4. Dispatch factory-review to an independent reviewer. The reviewer checks acceptance criteria, all canonical review gates, and test adequacy. Require PASS before QA; return FAIL findings to the implementer, and record BLOCKED with its next action.
5. After review PASS, dispatch factory-qa to an independent QA agent with the same base and candidate commits, worktree, ticket, and acceptance criteria.
6. Wait for all required CI checks. CI may finish while review or QA is running, but all evidence must name the same current candidate and base/integration state.
7. If review, QA, or CI finds a repair, or conflict resolution/base refresh changes the candidate, return it to the implementer. Repeat full local verification. After it passes, use factory-land to push the new candidate to the existing upstream issue branch and update the same PR; then repeat independent review, independent QA, and required CI. A FAIL or BLOCKED result means no merge, issue close, or next-ticket claim; record the finding or blocker and stop for repair or retry.
8. Use factory-land to arm auto-merge only after every required gate passes for the current candidate and base. Confirm the pull request is merged before closing the issue. Then clean up only the worktree created for that ticket.

The implementer, reviewer, and QA agent must be separate roles and contexts. The implementer cannot approve their own review or QA. Review and QA findings return to the implementer. A QA result of FAIL or BLOCKED never counts as PASS. Missing credentials, unavailable infrastructure, or an unreachable acceptance scenario must be recorded as BLOCKED with the evidence and next action. The already-open PR remains unmerged while a ticket is blocked; stop continuous execution until it is resolved.

Persist evidence in Beads comments: candidate and base commit IDs; build result and compiler/analyzer warnings; executed, passed, skipped, and failed counts for every required suite; island-test counts; convention results; review verdict and findings; QA verdict and acceptance-criteria evidence; pull request URL; required CI result; and confirmed merge state. Keep the issue notes as the concise current status. Use scratch files with Beads file arguments for multiline comments and descriptions.

Before ending or handing off any run, including when the current issue remains open because of FAIL or BLOCKED, run `bd dolt push` and confirm its durable state synced. If sync is unavailable, record that blocker and retry before ending. This does not close a blocked issue or permit claiming another ticket.

Never batch tickets, create rollup or integration branches, use staged or sealed lifecycle labels, or close an issue before confirmed merge. A blocked ticket stops continuous execution; resolve or hand it back with an actionable diagnosis before claiming new work.
