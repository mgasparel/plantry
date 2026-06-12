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
**merge to main** (serial, rebase-then-fast-forward, one at a time).

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

1. **Fetch latest main:**
   ```bash
   git fetch origin main
   ```

2. **Rebase the issue branch onto origin/main** (from inside the issue's
   worktree so git operates on the checked-out branch without conflicting with
   the main worktree):
   ```bash
   git -C ../worktrees/<issue-id> rebase origin/main
   ```

   If the rebase **produces conflicts**:
   - Abort: `git -C ../worktrees/<issue-id> rebase --abort`
   - Park post-PASS (the branch is good, but can't land cleanly right now):
     ```bash
     bd update <issue-id> --status blocked
     bd update <issue-id> --add-label needs-human
     bd update <issue-id> --notes "Auto-parked post-PASS: rebase conflict onto origin/main. Branch issue/<issue-id> preserved."
     ```
   - Skip to the next PASS verdict.

3. **Fast-forward main** to the rebased branch:
   ```bash
   git merge --ff-only issue/<issue-id>
   ```
   (Run from `code/`, which is checked out on `main`.)

   If `--ff-only` fails (main moved between fetch and merge): loop back to
   step 1 of this sequence and retry up to 3 times. After 3 failed attempts:
   park with reason `merge-race-exhausted` and skip to the next PASS verdict.

4. **Push main:**
   ```bash
   git push origin main
   ```

   If push fails: `git pull --rebase origin main` then retry once. If still
   fails: park with reason `push-failed` and skip to the next PASS verdict.

5. **Comment results and close:**
   ```bash
   bd update <issue-id> --notes "Merged to main. Critic passes: <pass_count>. Preflight: <preflight-path>. Advisories: <advisory-count> (see preflight report)."
   bd close <issue-id>
   ```

6. **Clean up:**
   ```bash
   git worktree remove ../worktrees/<issue-id> --force
   git branch -d issue/<issue-id>
   ```

7. Log: "MERGED: <issue-id> — <issue title>. Branch issue/<issue-id> landed on main."

### Step 3b — On FAILED verdicts

The worker already parked the issue (status=blocked, needs-human label, branch
preserved). No action needed here.

Log: "PARKED: <issue-id> — reason: <REASON>. Human review required."

### Step 4 — Continue loop

After all verdicts are processed, immediately return to Step 1.

---

## Safety invariants

- **Workers never touch main.** They commit only on `issue/<id>` inside their
  worktree.
- **Claiming is atomic before dispatch.** An issue is always in `in_progress`
  before a worker starts; `bd ready` returns only `status=open` issues, so a
  claimed issue cannot be double-dispatched.
- **Only green branches merge.** `RESULT: FAILED` issues are parked; their
  branches are never merged.
- **Merges are serial.** Even though workers run in parallel, merges execute
  one at a time — fetch → rebase → ff → push → next. This keeps `--ff-only`
  from racing against itself.
- **Rebase conflicts post-PASS park, not bypass.** The pre-flight passed but
  the branch can no longer land cleanly — park rather than force-merge or lose
  the work.
- **The loop's worst failure mode is idle**, not a broken main. If the
  orchestrator crashes mid-loop, in-progress issues stay `in_progress` (not
  re-claimable until manually reset). Branches are either clean (PASS, not yet
  merged) or quarantined (FAILED, parked).
