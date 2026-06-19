---
name: pipeline-orchestrator
description: >-
  Long-running autonomous development loop: atomically claims bd ready issues,
  dispatches implement-ticket-worker agents in parallel (up to MAX_WORKERS=3),
  and serially merges green branches onto main. USE FOR: "run the pipeline",
  "start autonomous development", "process the backlog hands-free". Invoke via
  /loop for a self-paced run. DO NOT USE FOR: implementing a single specific
  issue (use /implement-ticket directly).
license: MIT
metadata:
  author: plantry
  version: "2.0.0"
---

# pipeline-orchestrator

The long-running loop that closes the circuit between `bd ready` and a merged
commit on `main` with no human in the loop. It owns the three things that race
across concurrent workers: **issue selection** (atomic claim before dispatch),
**dispatch** (bounded parallel — up to `MAX_WORKERS=3` simultaneously), and
**merge to main** (serial, one PR at a time via `gh pr merge --auto`).

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

### Step 1 — Claim up to MAX_WORKERS ready issues

Build a dispatch list by repeating the following up to 3 times:

```bash
bd ready
```

- If `bd ready` returns nothing **and** the dispatch list is empty: output
  "No ready issues — loop idle." and call `ScheduleWakeup(delaySeconds=180)`.
  Return to start of loop. (180 s keeps the prompt cache warm during active
  development sessions.)

- If `bd ready` returns issues: pick the first one and claim it:

  ```bash
  bd update <issue-id> --claim
  ```

  Verify by checking `bd show <issue-id>` shows `status: in_progress`. If the
  claim failed (another process beat you), discard and try the next issue. Add
  successfully claimed IDs to the dispatch list.

  Repeat until the dispatch list has 3 entries or `bd ready` is empty.

### Step 2 — Dispatch workers in parallel

Spawn one `implement-ticket-worker` agent per claimed issue. **Send all agent
calls in a single response** so they execute concurrently:

```
Agent(subagent_type="implement-ticket-worker", prompt="<issue-id-1>")
Agent(subagent_type="implement-ticket-worker", prompt="<issue-id-2>")
Agent(subagent_type="implement-ticket-worker", prompt="<issue-id-3>")
```

Wait for **all** agents to return before proceeding. Parse each verdict block:

```
=== implement-ticket VERDICT ===
RESULT: PASS | FAILED
ISSUE: <issue-id>
BRANCH: issue/<issue-id>
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

The worker already parked the issue (status=blocked, needs-human label, branch
preserved). No action needed here.

Log: "PARKED: <issue-id> — reason: <REASON>. Human review required."

### Step 4 — Continue loop

After all verdicts are processed, immediately return to Step 1.

---

## Safety invariants

- **Workers never touch main.** They commit only on `issue/<id>` inside their
  worktree, then push to `origin/issue/<id>` and open a PR.
- **Claiming is atomic before dispatch.** An issue is always in `in_progress`
  before a worker starts; `bd ready` returns only `status=open` issues, so a
  claimed issue cannot be double-dispatched.
- **Only green branches merge.** `RESULT: FAILED` issues are parked; their
  PRs are left open for human review.
- **Merges are serial.** Even though workers run in parallel, each `gh pr merge
  --auto` call is made and awaited one at a time — enabling auto-merge → wait
  for confirmed merge → close bead → next. This prevents race conditions in
  `bd close` and worktree cleanup.
- **`bd close` only fires post-merge.** A bead is never closed for a PR that
  later fails CI. The poll loop confirms `state == "MERGED"` before closing.
- **The loop's worst failure mode is idle**, not a broken main. If the
  orchestrator crashes mid-loop, in-progress issues stay `in_progress` (not
  re-claimable until manually reset). Branches are either armed for auto-merge
  (PASS, awaiting CI) or quarantined (FAILED, parked).
