---
name: pipeline-orchestrator
description: >-
  Long-running autonomous development loop: claims one bd ready issue,
  dispatches an implement-ticket-worker, waits for the PR to merge via
  gh pr merge --auto, closes the bead, then claims the next. USE FOR:
  "run the pipeline", "start autonomous development", "process the backlog
  hands-free". Invoke via /loop for a self-paced run. DO NOT USE FOR:
  implementing a single specific issue (use /implement-ticket directly).
license: MIT
metadata:
  author: plantry
  version: "3.0.0"
---

# pipeline-orchestrator

The long-running serial loop that closes the circuit between `bd ready`
and a merged commit on `main` with no human in the loop.

**Serial model:** one issue at a time — claim → implement → PR green →
merge → close → next. The GitHub merge queue (enabled by branch protection)
enforces CI before the merge lands; this driver just orchestrates the
sequence.

## Invocation

Run as a self-paced loop:

```
/loop /pipeline-orchestrator
```

Or for a single pass (useful for testing):

```
/pipeline-orchestrator
```

---

## Per-iteration procedure

### Step 1 — Claim one ready issue

```bash
bd ready
```

- **Nothing ready:** output "No ready issues — loop idle." and call
  `ScheduleWakeup(delaySeconds=180)`. (180 s keeps the prompt cache warm
  during active development sessions.) Return to start of loop.

- **Issues available:** pick the first one and claim it:
  ```bash
  bd update <issue-id> --claim
  ```
  Verify `bd show <issue-id>` shows `status: in_progress`. If the claim
  failed (another process beat you), try the next issue.

### Step 2 — Dispatch implement-ticket-worker

Spawn one worker for the claimed issue:

```
Agent(subagent_type="implement-ticket-worker", prompt="<issue-id>")
```

Wait for the agent to return. Parse the verdict block:

```
=== implement-ticket VERDICT ===
RESULT: PASS | FAILED
ISSUE: <issue-id>
BRANCH: issue/<issue-id>
PR: <pr-url>
WORKTREE: ../worktrees/<issue-id>
...
```

### Step 3 — Serial merge for each PASS verdict

For each `RESULT: PASS`, in the order issues were dispatched (oldest first):

Parse the `PR:` field from the verdict block to get the PR URL (e.g.
`https://github.com/owner/repo/pull/123`). Extract the PR number from the URL.

1. **Enable auto-merge:**
   ```bash
   gh pr merge <pr-number> --auto --merge
   ```

   `--auto` queues the merge for when all required status checks pass. `--merge`
   creates a merge commit (preserves branch history; consistent with the local
   merge commits the old flow produced). Once branch protection is active this
   waits for CI + required reviewers; before it's active GitHub merges immediately.

   If `gh pr merge` fails (PR already merged, closed, or auth error):
   - If already merged: proceed to step 3 (poll will confirm quickly).
   - Otherwise: park with reason `merge-failed:<gh-error>` and skip to the next PASS verdict.

2. **Wait for the merge to complete** (poll every 30 s, timeout 30 min):
   ```bash
   gh pr view <pr-number> --json state,mergedAt --jq '.state + " " + (.mergedAt // "null")'
   ```

   Loop until `state == "MERGED"`. On timeout: park with reason `merge-timeout`
   (CI may still be running; the branch is preserved, auto-merge is armed).

   While waiting, if `state == "CLOSED"` (PR closed without merge): park with
   reason `pr-closed-unmerged` and skip to next PASS verdict.

3. **Comment results and close** (only after merge confirmed):
   ```bash
   bd update <issue-id> --notes "Merged to main via PR #<pr-number>. Critic passes: <pass_count>. Preflight: <preflight-path>. DEFER follow-ups: <bead-ids or 'none'>. NOTE: <note-count> (see preflight report and issue comments)."
   bd close <issue-id>
   ```

4. **Clean up** (after `bd close` succeeds):
   ```bash
   git worktree remove ../worktrees/<issue-id> --force
   git branch -d issue/<issue-id>
   ```

   If the worktree directory is locked (Windows build artifacts): skip `--force`
   removal and log the path — the branch is deleted; cosmetic cleanup only.

5. Log: "MERGED: <issue-id> — <issue title>. PR #<pr-number> landed on main."

### Step 3b — On FAILED verdicts

The worker already parked the issue (status=blocked, needs-human label).
No action needed here.

Log: "PARKED: <issue-id> — reason: <REASON>. Human review required."

### Step 4 — Continue loop

Return immediately to Step 1.

---

## Safety invariants

- **Workers never touch main.** They commit on `issue/<id>` and open a PR.
- **Claiming is atomic before dispatch.** An issue is always in `in_progress`
  before a worker starts; `bd ready` returns only `status=open` issues.
- **Only green PRs merge.** `RESULT: FAILED` issues are parked; their PRs
  are left open for human review.
- **`bd close` only fires post-merge.** A bead is never closed for a PR that
  later fails CI. The poll loop confirms `state == "MERGED"` first.
- **The loop's worst failure mode is idle**, not a broken main. If the
  orchestrator crashes mid-loop, in-progress issues stay `in_progress` and
  their auto-merge-armed PRs remain open — nothing is lost.
