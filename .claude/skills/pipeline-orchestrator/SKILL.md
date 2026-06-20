---
name: pipeline-orchestrator
description: >-
  Long-running autonomous development loop: claims one bd ready issue,
  dispatches an implement-ticket-worker, waits for the PR to merge via
  gh pr merge --auto, closes the bead, then claims the next. Includes CI
  reconcile loop: polls gh pr checks, classifies red CI, reruns flakes once,
  dispatches ci-fix-worker for code/test failures, and parks env/config +
  exhausted cases as ci-failed. USE FOR: "run the pipeline", "start autonomous
  development", "process the backlog hands-free". Invoke via /loop for a
  self-paced run. DO NOT USE FOR: implementing a single specific issue (use
  /implement-ticket directly).
license: MIT
metadata:
  author: plantry
  version: "4.0.0"
---

# pipeline-orchestrator

The long-running serial loop that closes the circuit between `bd ready`
and a merged commit on `main` with no human in the loop.

**Serial model:** one issue at a time — claim → implement → PR green →
merge → close → next. When a GitHub merge queue is configured on `main`
(`merge_group:` trigger in `ci.yml` + "Require merge queue" branch
protection), `gh pr merge --auto` enqueues the PR and the queue rebases it
onto the live tip of `main` and re-runs CI before the merge lands — so a PR
is revalidated against the real merge result, not just its own branch. If no
queue is configured, `--auto` is plain auto-merge (CI on the branch in
isolation); the mergeability guard in Step 3 is what catches a PR that has
fallen behind `main` in that case. This driver orchestrates the sequence and
runs a CI reconcile loop to auto-fix red CI before giving up.

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

### Step 0 — Housekeeping

Prune stale worktree registrations and merged local branches before claiming
new work. On Windows the post-merge cleanup in Step 3 skips worktree dirs that
are locked by build artifacts, so they accumulate across iterations; this sweep
self-heals that leak. It only touches already-merged/abandoned state — never an
in-progress branch.

```bash
git worktree prune
git fetch origin main --quiet
git branch --merged origin/main | grep -E '^\s+issue/' | xargs -r git branch -d
```

`git worktree prune` drops registrations whose directory is already gone.
Issue branches land on `origin/main` (via the merge queue), not local `main`,
so the merged check is against the freshly-fetched `origin/main`; `git branch -d`
(safe delete) only removes `issue/*` branches fully merged there, leaving any
unmerged in-flight branch untouched. Log nothing unless something was reaped.

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

For each `RESULT: PASS`, parse the `PR:` field from the verdict block to get
the PR URL (e.g. `https://github.com/owner/repo/pull/123`). Extract the PR
number from the URL. Also retain the `WORKTREE:` path — it is passed to
`ci-fix-worker` if CI goes red.

Initialise per-PR state:
- `ci_fix_attempts = 0`
- `flake_rerun_done = false`

1. **Guard mergeability, then enable auto-merge:**

   First check the PR can actually merge — a PR cut from a base that `main` has
   since advanced past may be behind or conflicting. Without a merge queue,
   arming auto-merge on such a PR just burns the full 30-min poll into a
   `merge-timeout`; catch it up front instead:
   ```bash
   gh pr view <pr-number> --json mergeStateStatus --jq '.mergeStateStatus'
   ```
   - `DIRTY` (merge conflict with `main`) → park with reason `merge-conflict`
     and skip to the next PASS verdict. The branch and worktree are preserved
     for a human to rebase.
   - `BEHIND` **and no merge queue is configured** → the PR cannot fast-forward
     and nothing will advance it; park with reason `merge-conflict:behind` and
     skip to the next PASS verdict. (With a merge queue, `BEHIND` is fine — the
     queue rebases it — so proceed.)
   - `CLEAN`, `UNSTABLE`, `HAS_HOOKS`, `BLOCKED`, `BEHIND` (queue configured),
     or `UNKNOWN` → proceed to arm auto-merge below.

   ```bash
   gh pr merge <pr-number> --auto --merge
   ```

   `--auto` queues the merge for when all required status checks pass. `--merge`
   creates a merge commit (preserves branch history; consistent with the local
   merge commits the old flow produced). Once branch protection is active this
   waits for CI + required reviewers; before it's active GitHub merges immediately.

   If `gh pr merge` fails (PR already merged, closed, or auth error):
   - If already merged: proceed to step 2 (poll will confirm quickly).
   - Otherwise: park with reason `merge-failed:<gh-error>` and skip to the
     next PASS verdict.

2. **Poll for merge or red CI** (every 30 s, overall timeout 30 min):

   On each tick, run both checks in sequence:

   a. Check PR state:
   ```bash
   gh pr view <pr-number> --json state,mergedAt --jq '.state + " " + (.mergedAt // "null")'
   ```
   - `state == "MERGED"` → exit the poll loop, proceed to step 3 (close + cleanup).
   - `state == "CLOSED"` → park with reason `pr-closed-unmerged`; skip to
     next PASS verdict.

   b. If not yet merged, check CI checks:
   ```bash
   gh pr checks <pr-number> --json name,state,conclusion \
     --jq '[.[] | select(.state == "COMPLETED")] | {total: length, failed: [.[] | select(.conclusion == "FAILURE" or .conclusion == "CANCELLED" or .conclusion == "TIMED_OUT")] | length}'
   ```
   - Any check failed (`failed > 0`) → jump to **Step 3c** (CI reconcile).
   - All completed with no failures → continue waiting (GitHub will merge via
     auto-merge shortly; keep polling state).
   - Checks still in progress → continue the poll loop.

   On timeout (30 min): park with reason `merge-timeout`
   (CI may still be running; the branch is preserved, auto-merge is armed).

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

### Step 3c — CI reconcile loop

Entered when `gh pr checks` reports at least one failed check. The worktree
at the `WORKTREE:` path from the implement-ticket-worker verdict is still on
disk (the worker left it in place per its cleanup-timing rule).

**Constants:** `MAX_CI_FIX_ATTEMPTS = 3`.

**Step 3c-1 — Get the failing run ID:**
```bash
gh run list --branch issue/<issue-id> --json databaseId,conclusion,status \
  --jq '[.[] | select(.status == "completed" and (.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out"))] | first | .databaseId'
```

If no run ID is returned (checks failed but no run found — race condition):
wait 10 s and re-enter the main poll loop at step 2.

**Step 3c-2 — Fetch the failing logs and classify:**
```bash
gh run view <run-id> --log-failed
```

Classify the failure from the log output using these heuristics (check in
order; first match wins):

| Class | Log signals | Action |
|-------|-------------|--------|
| **Flaky / transient** | `timeout`, `timed out`, `flaky`, `rate limit`, `too many requests`, `runner`, `network error`, `connection reset`, `SIGKILL`, `OOMKilled`, `infrastructure failure` — OR the failure is in a known-nondeterministic step (Docker pull, E2E boot) | Step 3c-3 |
| **Env / config** | `secret`, `environment variable`, `GITHUB_TOKEN`, `configuration`, `not found` on a non-code resource, `permission denied` on a non-test resource, `runner image`, `branch protection` | Step 3c-4 |
| **Code / test** | Everything else — compilation errors, assertion failures, missing files, case-sensitivity issues, line-ending issues | Step 3c-5 |

When signals from multiple classes appear, prefer the higher-severity class
(code/test > env/config > flaky).

**Step 3c-3 — Rerun for flake (at most once per PR):**

- If `flake_rerun_done == false`:
  ```bash
  gh run rerun <run-id> --failed
  ```
  Set `flake_rerun_done = true`. Log: "Rerunning CI for flake on PR #<pr-number> (run <run-id>)."
  Return to the main poll loop at step 2 to wait for the rerun result.

- If `flake_rerun_done == true` (second flake signal after a rerun): reclassify
  as **code/test** — a failure that persists after one rerun is not transient.
  Proceed to Step 3c-5.

**Step 3c-4 — Park for env/config failure:**

```bash
bd update <issue-id> --status blocked
bd update <issue-id> --add-label needs-human
bd update <issue-id> --notes "Auto-parked <timestamp>: ci-failed (env/config). Run: <run-id>. PR #<pr-number>."
bd comment <issue-id> "CI reconcile: parked ci-failed (env/config). Run <run-id> failed with environment or configuration error — not a code fix. Log excerpt: <first 20 lines of gh run view output>. Branch issue/<issue-id> and worktree <worktree-path> preserved."
```

Log: "PARKED: <issue-id> — ci-failed (env/config). Human review required."
Skip to next PASS verdict.

**Step 3c-5 — Dispatch ci-fix-worker for code/test failure:**

If `ci_fix_attempts >= MAX_CI_FIX_ATTEMPTS` → go to Step 3c-6.

Increment `ci_fix_attempts`. Log: "CI fix attempt <ci_fix_attempts>/<MAX_CI_FIX_ATTEMPTS> for PR #<pr-number> (run <run-id>)."

Spawn a ci-fix-worker sub-agent:
```
Agent(
  subagent_type="ci-fix-worker",
  prompt="<issue-id> <pr-number> <run-id>"
)
```

The ci-fix-worker returns one of three verdicts:
- `FIXED` — patch applied, local build+test passed, pushed to `issue/<id>`.
  Return to the main poll loop at step 2 to wait for CI to re-run.
- `RERUN` — worker classified the failure as flaky-transient. Run
  `gh run rerun --failed <run-id>` once if `flake_rerun_done == false`
  (set `flake_rerun_done = true`), then return to the poll loop. If
  `flake_rerun_done` is already `true`, proceed to Step 3c-6 instead.
- `PARKED` — the worker already parked the issue (set status blocked +
  needs-human + notes/comment). Do **not** re-park. Log "ci-fix-worker
  parked <issue-id>: <reason>" and skip to the next PASS verdict.

**Step 3c-6 — Park for exhausted CI fix attempts:**

```bash
bd update <issue-id> --status blocked
bd update <issue-id> --add-label needs-human
bd update <issue-id> --notes "Auto-parked <timestamp>: ci-failed (exhausted after <ci_fix_attempts> fix attempt(s)). Run: <run-id>. PR #<pr-number>."
bd comment <issue-id> "CI reconcile: parked ci-failed after <ci_fix_attempts> fix attempt(s). Last failing run: <run-id>. Branch issue/<issue-id> and worktree <worktree-path> preserved for human review."
```

Log: "PARKED: <issue-id> — ci-failed (exhausted). Human review required."
Skip to next PASS verdict.

---

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
- **Flake reruns happen at most once per PR.** A second failure after a rerun
  is treated as a code/test failure, not an infinite flake loop.
- **CI fix attempts are bounded.** At most `MAX_CI_FIX_ATTEMPTS` (3) ci-fix-worker
  dispatches per PR; exhaustion parks the issue rather than looping forever.
- **Worktrees are preserved on park.** When parking for any CI reason, the
  worktree and branch are left intact for human inspection — never cleaned up.
- **The loop's worst failure mode is idle**, not a broken main. If the
  orchestrator crashes mid-loop, in-progress issues stay `in_progress` and
  their auto-merge-armed PRs remain open — nothing is lost.
