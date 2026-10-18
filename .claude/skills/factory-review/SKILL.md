---
name: factory-review
description: Independently review a Plantry ticket candidate against its acceptance criteria, canonical architectural gates, and behavioral test adequacy.
license: MIT
metadata:
  author: plantry
  version: "1.0.0"
---

# Factory review

Dispatch a fresh, independent reviewer for each candidate commit. Give the reviewer the ticket, worktree, base commit, candidate commit, and local suite evidence. The reviewer must not implement changes or approve a different commit than the one named in the request.

Read .claude/review-criteria.md and apply every gate and its FIX, DEFER, and NOTE rules. Verify every acceptance criterion against the actual candidate. Review committed changes and confirm the worktree contains no uncommitted or omitted ticket files. If the candidate or base commit differs from the supplied IDs, return BLOCKED.

The reviewer assesses test adequacy, not test quantity. For changed behavior, ask whether the acceptance criteria and relevant invariants are protected; whether important negative paths are exercised; whether a plausible defect could pass the existing tests; whether assertions prove meaningful outcomes; whether tests run at the right layer; and whether they are brittle, redundant, or over-mocked. Existing tests may be sufficient. Do not request tests merely to increase counts, execute a mutation engine, or satisfy a percentage.

A blocking test finding must name the concrete behavior or invariant not protected, explain why the current tests would miss a plausible defect, and prescribe a specific scenario and outcome assertion that would close the gap. A request for more tests without this evidence is not a blocking finding.

Return exactly one verdict:

- PASS when acceptance criteria are met and no FIX finding remains. DEFER and NOTE follow the canonical criteria; a DEFER may not hide an acceptance gap or unverified behavior.
- FAIL when one or more FIX findings remain. Send the findings back to the implementer.
- BLOCKED when the named candidate cannot be reviewed, required evidence is missing, or a genuine owner decision is needed.

Every result names the ticket, base commit, candidate commit, overall verdict, test-adequacy assessment, and findings with file and line references where applicable. Include explicit, self-contained repair instructions for FIX findings and the precise boundary trigger and recommendation for DEFER findings. Record the result in Beads. A later candidate requires a fresh reviewer and new result.
