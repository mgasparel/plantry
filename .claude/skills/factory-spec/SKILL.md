---
name: factory-spec
description: Prepare a Plantry work request as a clear, dependency-aware Beads issue or epic before implementation.
license: MIT
metadata:
  author: plantry
  version: "1.0.0"
---

# Factory spec

Turn an approved work request into a ticket an independent implementer can complete and verify. Use this skill for scoping, acceptance criteria, and dependency planning. Use factory-run to execute work.

Read the relevant source, project instructions, and ADRs before writing the spec. Prefer an existing decision or pattern over an unnecessary question. Ask the owner only when a product choice, threshold, or consequential design fork is genuinely unsettled.

Write one issue per independently mergeable change. State the problem, desired behavior, acceptance criteria, useful design constraints, and explicit in-scope and out-of-scope boundaries. Make each criterion observable through a user-visible result, persisted state, or a concrete artifact check. Name relevant negative cases and invariants when they matter. Do not prescribe test counts, coverage percentages, or implementation details that do not affect the outcome.

Use epics only to group planned work and express dependencies. Epics are never implementation branches or delivery batches. Add a blocking dependency only when the dependent issue cannot be built or verified before its blocker lands; use priority for sequencing preferences.

Keep a ticket small enough for one branch, one pull request, one independent review, and one independent QA pass. Split unrelated deliverables. Do not create rollups, staged labels, sealing states, or batch membership.

Create or update Beads records only when the user asked for issue work. On PowerShell, write free-form bodies to a scratch file and use bd create or bd update with --body-file. After writing, read the issue back with bd show and confirm that the stored acceptance criteria and dependencies match the approved request.

If a required owner decision is missing, record the exact decision and its alternatives on the issue and leave it unclaimed. Do not start implementation while acceptance criteria depend on that decision.

After the owner confirms the complete specification, record the agreed decisions in the issue and add the spec:ready label last. Never mark unresolved or parked specifications ready. Specification approval authorizes dispatch preparation; it does not itself start implementation.
