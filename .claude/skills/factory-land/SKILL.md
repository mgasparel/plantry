---
name: factory-land
description: Create one Plantry ticket pull request after local checks, then automatically merge and close it only after all delivery gates pass.
license: MIT
metadata:
  author: plantry
  version: "1.0.0"
---

# Factory land

Create and land one ticket branch and its one pull request. The approved factory design authorizes automatic merge after all gates pass; do not ask for that authorization again. Tool, sandbox, branch-protection, and network limits still apply. If an operation is denied or unavailable, record the blocker and stop without bypassing the environment.

After local verification passes, confirm the issue branch and worktree belong to the ticket and the current candidate matches the successful local evidence. Push the issue branch with upstream tracking (`git push -u origin issue/<ticket-id>`); never push directly to `main`. Open one PR from issue/<ticket-id> to main before independent review and QA. Put the acceptance criteria and concise build and suite evidence in the body, and link the durable Beads evidence. Keep this PR open through any repair; never create a second PR for the same ticket.

Always require the CI workflow fast job to complete successfully, even when branch protection is absent or GitHub lists no required checks. Also require any additional checks mandated by repository protection. An empty required-check list, missing job, skipped job, or pending workflow is not a CI pass. Inspect the check runs for the current PR head and integration base.

Wait for independent review PASS, independent QA PASS, and every required CI check to apply to the same current candidate and base/integration state. CI may complete before review or QA. Inspect failing CI logs and distinguish code/test failures from infrastructure failures; return code/test failures to the implementer with the failing evidence. A QA BLOCKED result records the unavailable resource, the exact check that could not run, and the retry action; leave the PR unmerged and stop continuous execution. A clearly transient CI infrastructure failure may be rerun once; if it repeats, or if credentials/configuration are missing, mark the ticket BLOCKED. Do not enable auto-merge while any required check is pending, failing, or blocked.

Any source change, CI repair, merge-conflict resolution, rebase, or base refresh that changes the candidate or integration state invalidates prior evidence. Return code changes to the implementer and require new full local verification. After it passes, push the new candidate to the existing upstream issue branch so the same PR and its CI reflect that commit. Then require fresh independent review, independent QA, and required CI for the resulting candidate. Before merge, refresh origin/main and confirm the PR is mergeable and up to date. Evidence from an older commit or base does not carry forward.

Only after the current candidate has passing local checks, independent review, independent QA, and required CI, arm automatic merge with the repository's approved merge method. Use gh pr merge with --match-head-commit <candidate-sha> to reject a changed PR head; recheck the integration base immediately before requesting merge. Confirm the PR state is MERGED and record its URL, merged commit, and successful gate evidence in Beads. Close the Beads issue only after that confirmation. Then run `bd dolt push` and confirm the Beads evidence synced before ending the session or claiming another ticket; if the sync is unavailable, record the blocker and retry before proceeding.

After confirmed merge, remove only the worktree created for this ticket. Preserve the branch until merge confirmation, and never delete another worktree, branch, or Docker resource. In continuous mode, return to factory-run only after the issue is confirmed merged and closed.
