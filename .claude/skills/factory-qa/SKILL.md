---
name: factory-qa
description: Independently exercise a Plantry ticket candidate against its acceptance criteria on an isolated ephemeral Aspire stack.
license: MIT
metadata:
  author: plantry
  version: "1.0.0"
---

# Factory QA

Dispatch a fresh QA agent for each candidate commit. Give it the ticket, worktree, base commit, candidate commit, and acceptance criteria. QA is independent of implementation and review: it does not edit source or test files, and the implementer cannot report its own QA result.

Before booting, verify HEAD equals the supplied candidate, the supplied base is the expected integration base, and git status --porcelain has no tracked or untracked changes. Ignored build outputs and evidence are allowed; source or configuration edits invalidate the candidate. Recheck HEAD and worktree cleanliness after exercising the stack. A mismatch is BLOCKED and no passing result may be recorded for it. Record both base and candidate IDs in the QA evidence.

Run the candidate from its worktree using the isolated Aspire procedure in docs/Engineering/worktree-verification.md. Set PLANTRY_VERIFY for an ephemeral database, use isolated Aspire ports, verify the required resources and successful migrations, and clean up only the stack launched from this worktree. Never stop or remove the primary stack or its named database volume.

Prepare the minimum useful scenario data. The current Dev seed creates products, recipes, inventory, price observations, meal plans and attendees, plus Deals review data for the demo household. Use the real UI to add further data when practical. Exercise the actual UI or public backend entry point affected by the ticket. Verify visible outcome and persisted state, including relevant rejection, invalid-input, and regression paths. Inspect browser console and network activity, Aspire resource logs, and application telemetry for errors related to the scenario.

For documentation or tooling changes, still boot the isolated app and verify its health and migrations, then directly validate the changed artifact with the relevant parser, validator, invocation, or reference check. A smoke boot does not replace direct artifact validation.

Return one verdict: PASS, FAIL, or BLOCKED. PASS requires evidence for every acceptance criterion on the named candidate commit. FAIL names the observed behavior and evidence that contradicts a criterion. BLOCKED names the unavailable resource, credential, or scenario and the exact check that could not be completed. Do not treat an infrastructure failure, inaccessible scenario, or missing test data as PASS.

Record the base and candidate commits, cleanliness checks, stack resources, scenario data, steps, observed results, persistence checks, relevant browser/network/log evidence, and cleanup result in a Beads comment. If the candidate changes, discard the old verdict and run QA again.
