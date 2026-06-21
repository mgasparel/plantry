---
name: pipeline-orchestrator
description: >-
  Long-running autonomous development loop. Claims one bd ready issue, routes it
  to an epic integration branch (a curated epic, or the rolling rollup for loose
  one-offs), dispatches an implement-ticket-worker, and merges the worker's child
  branch into the epic — no per-child PR. When the whole epic is complete it opens
  ONE epic→main PR, runs the CI reconcile loop (poll gh pr checks, rerun flakes
  once, dispatch ci-fix-worker, park exhausted/env cases), and on merge batch-closes
  every child + the epic. USE FOR: "run the pipeline", "start autonomous development",
  "process the backlog hands-free". Invoke via /loop for a self-paced run. DO NOT USE
  FOR: implementing a single specific issue (use /implement-ticket directly).
license: MIT
metadata:
  author: plantry
  version: "5.0.0"
---

# pipeline-orchestrator

The long-running serial loop that closes the circuit between `bd ready` and a
merged commit on `main` — **one PR per epic, not per issue.**

**Why batch (v5):** after the CI cost change (`plantry-49hm`), per-PR CI is a fast
gate only and the full suite + deploy moved to the release tag. The remaining lever
is the *number* of PRs. So every issue flows through an **epic integration branch**:
children merge into `epic/<epic-id>` with no per-child PR and no per-child CI, and the
epic ships as **one** `epic→main` PR when it is 100% complete. A 10-child epic that
used to cost 10 PRs/merges/CI-runs now costs one. See `plantry-ekoo`.

**Serial model:** one issue at a time, and **drain the current epic before claiming
unrelated work** so the epic branch never falls behind `main`. There is no merge queue
(unavailable on this personal-account repo; deferred until org-owned — ADR-016). Two
things keep `main` safe: (1) the epic branch is **rebased onto fresh `origin/main`
immediately before its PR is opened** (the safety net for a concurrent human merge);
(2) the mergeability guard parks a behind/conflicting epic PR rather than landing it
stale.

**Two kinds of epic:**

| | Curated epic (e.g. `plantry-hcj3`) | Rollup epic (catch-all) |
|---|---|---|
| Source | a real feature; children defined in beads | loose one-offs the loop auto-attaches |
| Identity | the epic bead id | a `type=epic` bead labelled `rollup`; **identity is the bead id** (branch `epic/<bead-id>`), title is just a dated label. Only ever one open unsealed rollup at a time. |
| Membership | fixed | open until **sealed** (human applies label `sealed`, or auto at `ROLLUP_MAX_CHILDREN`) |
| Ships when | 100% of children staged | sealed **and** 100% of (now-fixed) children staged |

**Constants:**
- `ROLLUP_MAX_CHILDREN = 8` — at this many children a rollup auto-seals and a fresh one opens.
- `MAX_CI_FIX_ATTEMPTS = 3` — ci-fix-worker dispatches per epic PR before parking.

## Invocation

```
/loop /pipeline-orchestrator      # self-paced loop
/pipeline-orchestrator            # single pass (testing)
```

---

## Per-iteration procedure

### Step 0 — Housekeeping

Prune stale registrations before claiming new work. Only touches already-merged or
abandoned state — never an in-progress branch or an active epic.

```bash
git worktree prune
git fetch origin --quiet
git branch --merged origin/main | grep -E '^\s+(issue|epic)/' | xargs -r git branch -d
```

`git branch -d` (safe delete) removes only `issue/*` and `epic/*` branches already
merged into `origin/main`, leaving any in-flight child or active epic untouched. Log
nothing unless something was reaped.

### Step 1 — Claim one ready issue and resolve its epic

```bash
bd ready
```

- **Nothing ready:** output "No ready issues — loop idle." and call
  `ScheduleWakeup(delaySeconds=180)`. Return to the start of the loop.

- **Issues available — prefer draining the active epic.** If an epic is already in
  progress (an `epic/<id>` branch with staged-but-unmerged children exists), pick the
  first ready issue **that is a child of that epic**. Only if the active epic has no
  ready children do you pick a ready issue from elsewhere — and you do *not* start its
  epic until the active one has shipped or parked. This keeps the epic branch from
  falling behind.

Claim the chosen issue:
```bash
bd update <issue-id> --claim
```
Verify `bd show <issue-id>` shows `status: in_progress`. If the claim failed (another
process beat you), try the next ready issue.

**Resolve the epic the issue belongs to:**

- **Has a parent epic** (`bd show` lists a `Parent:`) → `epic-id` = that parent.
- **No parent** (a loose one-off) → attach it to the current rollup:
  ```bash
  # Find a usable rollup: labelled `rollup`, not yet `sealed`. bd list excludes
  # closed by default and has no --type/--status filter — the `rollup` label is
  # what identifies these epics.
  bd list --label rollup --exclude-label sealed
  ```
  - If one is returned and has `< ROLLUP_MAX_CHILDREN` children (check via `bd show`)
    → use it.
  - Otherwise create a fresh rollup. Its **identity is the new bead id** (the branch
    will be `epic/<that-id>`); the title is just a human-readable dated label — do NOT
    key on it. The date stamp is "when opened," not a unique slot, so multiple rollups
    can share a date without conflict (different bead ids, and only one is ever open
    unsealed at a time):
    ```bash
    bd create --type epic --labels rollup --title "rollup (opened $(date -u +%Y-%m-%d))" \
      --description "Rolling catch-all batch of loose one-offs (bugfixes/chores). Ships as one PR when sealed (label 'sealed' or ROLLUP_MAX_CHILDREN children), after which a fresh rollup opens for subsequent one-offs." --priority 2
    ```
  Attach the issue and set `epic-id` to the rollup:
  ```bash
  bd update <issue-id> --parent <rollup-id>
  ```

**Ensure the epic integration branch + worktree exist.** If `epic/<epic-id>` is not yet
present, create it off fresh `origin/main` in its own worktree (the orchestrator merges
children into it here):
```bash
git show-ref --verify --quiet refs/heads/epic/<epic-id> || \
  git worktree add ../worktrees/<epic-id> -b epic/<epic-id> origin/main
```

### Step 2 — Dispatch implement-ticket-worker

```
Agent(subagent_type="implement-ticket-worker", prompt="<issue-id>")
```

The worker derives its base from the issue's parent (`epic/<epic-id>`), branches its
child worktree off it, pre-flights locally, commits, and returns — **without** pushing
or opening a PR. Wait for the verdict:

```
=== implement-ticket VERDICT ===
RESULT: PASS | FAILED
ISSUE: <issue-id>
EPIC: <epic-id>
BRANCH: issue/<issue-id>
BASE: epic/<epic-id>
WORKTREE: ../worktrees/<issue-id>
...
```

### Step 3 — Integrate the child (per verdict)

**On `RESULT: FAILED`:** the worker already parked the issue (`blocked` + `needs-human`).
The epic now has a blocked child, so it cannot reach 100% and will not flush — that is
the intended "a parked child blocks its batch" behaviour; a human clears it later. Log
`PARKED: <issue-id> — <REASON>` and go to Step 4 (the epic is not ready; the loop moves
on to other work).

**On `RESULT: PASS`:** merge the child into the epic branch, then label it staged. No
main PR, no `bd close` yet.

```bash
git -C ../worktrees/<epic-id> merge --no-ff issue/<issue-id> \
  -m "Integrate <issue-id> into epic/<epic-id>"
git -C ../worktrees/<epic-id> push -u origin epic/<epic-id>
bd update <issue-id> --add-label staged
```

- Pushing the epic branch backs it up and makes it visible; **no CI fires** (`ci.yml`
  triggers only on a PR to `main` or a push to `main`).
- If the merge **conflicts** (should not happen under serial work, but a human may have
  touched the epic): abort it, park the child `merge-conflict-into-epic`
  (`bd update <issue-id> --status blocked --add-label needs-human`, comment the detail),
  leave the child worktree for a human, and go to Step 4.

Then remove the integrated child's worktree (its commit is now on the epic branch):
```bash
git worktree remove ../worktrees/<issue-id> --force
git branch -D issue/<issue-id>   # -D, not -d: the branch is merged into the EPIC, not main, so -d would refuse
```
If the worktree is locked by Windows build artifacts, skip `--force` removal and log the
path — the branch delete is cosmetic; the commit is safely on the epic branch.

### Step 4 — Flush check

Decide whether the epic is ready to ship. Read its child status via `bd show <epic-id>`.

**Rollup epics first auto-seal if needed:** if `epic-id` is a rollup, has no `sealed`
label, and now has `>= ROLLUP_MAX_CHILDREN` children → seal it and open a successor so
later one-offs don't pile onto a shipping batch:
```bash
bd update <epic-id> --add-label sealed
```

> **The orchestrator adds the `sealed` label in exactly one place: this auto-seal when >= ROLLUP_MAX_CHILDREN. Never add it for any other reason — including because all currently-staged children happen to be the ones named in a single user request.**

**The epic is READY TO FLUSH when every child is `staged` AND** either:
- it is a **curated** epic (membership is fixed — all children staged means 100%), or
- it is a **rollup** carrying the `sealed` label.

If **not ready** (a curated epic with children still to do, or an unsealed rollup, or any
child still open/in_progress/blocked) → return to **Step 1** to claim the next child of
this epic (draining it). A rollup that is unsealed and under capacity simply keeps
accepting one-offs across iterations.

If **ready** → run **Step 5 (Flush)**.

### Step 5 — Flush: one epic→main PR

Operate in the epic worktree `../worktrees/<epic-id>`.

1. **Rebase onto fresh `origin/main` (the safety net):**
   ```bash
   git fetch origin main --quiet
   git -C ../worktrees/<epic-id> rebase origin/main
   ```
   - Conflict → abort (`git -C ../worktrees/<epic-id> rebase --abort`), park the **epic**:
     ```bash
     bd update <epic-id> --status blocked --add-label needs-human
     bd update <epic-id> --notes "Auto-parked <ts>: epic-rebase-conflict on flush. Branch epic/<epic-id> + worktree preserved."
     bd comment <epic-id> "Flush blocked: epic/<epic-id> conflicts with origin/main on rebase. A human must rebase. Children remain staged; nothing closed."
     ```
     Log `PARKED: <epic-id> — epic-rebase-conflict` and return to Step 1.
   - Clean → force-push the rebased branch: `git -C ../worktrees/<epic-id> push --force-with-lease`.

2. **Open one PR for the whole epic:**
   ```bash
   gh pr create --base main --head epic/<epic-id> \
     --title "<epic title from bd show>" \
     --body "Ships epic <epic-id> as one batch. Children: <child-ids + one-line each>. Each child passed the worker's local pre-flight (build + full tests incl. E2E + Opus critic). Closes them on merge."
   ```
   Extract the PR number. Initialise `ci_fix_attempts = 0`, `flake_rerun_done = false`.

3. **Guard mergeability, then arm auto-merge** (the epic PR is gated by the `fast` check):
   ```bash
   gh pr view <pr-number> --json mergeStateStatus --jq '.mergeStateStatus'
   ```
   - `DIRTY` → park the epic `merge-conflict` (as in step 1's conflict block) and return to Step 1.
   - `BEHIND` → park the epic `merge-conflict:behind`; return to Step 1.
   - `CLEAN` / `UNSTABLE` / `HAS_HOOKS` / `BLOCKED` / `UNKNOWN` → arm:
     ```bash
     gh pr merge <pr-number> --auto --merge
     ```
   If `gh pr merge` fails: already-merged → continue to step 4; else park `merge-failed:<err>`.

4. **Poll for merge or red CI** (every 30 s, overall timeout 30 min):

   a. PR state:
   ```bash
   gh pr view <pr-number> --json state,mergedAt --jq '.state + " " + (.mergedAt // "null")'
   ```
   - `MERGED` → go to step 5 (batch-close + cleanup).
   - `CLOSED` → park the epic `pr-closed-unmerged`; return to Step 1.

   b. If not merged, CI checks:
   ```bash
   gh pr checks <pr-number> --json name,state,conclusion \
     --jq '[.[] | select(.state == "COMPLETED")] | {total: length, failed: [.[] | select(.conclusion == "FAILURE" or .conclusion == "CANCELLED" or .conclusion == "TIMED_OUT")] | length}'
   ```
   - `failed > 0` → **Step 6 (CI reconcile)**.
   - all complete, none failed → keep polling (auto-merge will land it).
   - still in progress → keep polling.

   Timeout (30 min) → park the epic `merge-timeout` (branch + worktree preserved, auto-merge armed); return to Step 1.

5. **Batch-close on confirmed merge:**
   ```bash
   # Close every child of the epic, then the epic.
   for child in <all child ids from bd show <epic-id>>; do
     bd update "$child" --notes "Merged to main in epic PR #<pr-number> (epic <epic-id>)."
     bd update "$child" --remove-label staged
     bd close "$child"
   done
   bd update <epic-id> --notes "Shipped via epic PR #<pr-number>. <n> children landed on main."
   bd close <epic-id>
   ```

6. **Clean up:**
   ```bash
   git worktree remove ../worktrees/<epic-id> --force
   git branch -D epic/<epic-id>   # -D: local main may not yet reflect the just-merged epic
   ```
   Locked worktree (Windows) → skip `--force`, log the path; the branch delete is cosmetic.

   Log: `MERGED: epic <epic-id> — <title>. <n> children landed on main via PR #<pr-number>.`
   (If the flushed epic was a rollup, the next loose one-off in Step 1 opens a fresh rollup.)

### Step 6 — CI reconcile loop (epic PR)

Entered when `gh pr checks` reports a failed check on the epic PR. The epic worktree at
`../worktrees/<epic-id>` is still on disk.

**Step 6-1 — Get the failing run ID:**
```bash
gh run list --branch epic/<epic-id> --json databaseId,conclusion,status \
  --jq '[.[] | select(.status == "completed" and (.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out"))] | first | .databaseId'
```
No run ID (race) → wait 10 s and re-enter Step 5's poll loop.

**Step 6-2 — Fetch logs and classify:**
```bash
gh run view <run-id> --log-failed
```

| Class | Log signals | Action |
|-------|-------------|--------|
| **Flaky / transient** | `timeout`, `timed out`, `rate limit`, `too many requests`, `runner`, `network error`, `connection reset`, `SIGKILL`, `OOMKilled`, `infrastructure failure` | Step 6-3 |
| **Env / config** | `secret`, `environment variable`, `GITHUB_TOKEN`, `configuration`, `not found` on a non-code resource, `permission denied` on a non-test resource, `runner image`, `branch protection` | Step 6-4 |
| **Code / test** | Everything else — compilation errors, assertion failures, missing files, case/line-ending issues | Step 6-5 |

Multiple classes → prefer the higher severity (code/test > env/config > flaky).

**Step 6-3 — Rerun for flake (at most once):**
- `flake_rerun_done == false` → `gh run rerun <run-id> --failed`; set `flake_rerun_done = true`;
  return to Step 5's poll loop.
- already `true` → reclassify as code/test → Step 6-5.

**Step 6-4 — Park for env/config:**
```bash
bd update <epic-id> --status blocked --add-label needs-human
bd update <epic-id> --notes "Auto-parked <ts>: ci-failed (env/config) on epic PR #<pr-number>. Run: <run-id>."
bd comment <epic-id> "CI reconcile: epic PR #<pr-number> failed with env/config error (run <run-id>) — not a code fix. Log excerpt: <first 20 lines>. epic/<epic-id> + worktree preserved; children remain staged."
```
Log `PARKED: epic <epic-id> — ci-failed (env/config)`; return to Step 1.

**Step 6-5 — Dispatch ci-fix-worker for code/test:**

`ci_fix_attempts >= MAX_CI_FIX_ATTEMPTS` → Step 6-6. Otherwise increment and dispatch
against the epic branch:
```
Agent(subagent_type="ci-fix-worker", prompt="<epic-id> <pr-number> <run-id>")
```
The worker operates on `epic/<epic-id>` / `../worktrees/<epic-id>` and returns:
- `FIXED` — patch pushed to `epic/<epic-id>`; return to Step 5's poll loop.
- `RERUN` — flaky; if `flake_rerun_done == false` run `gh run rerun <run-id> --failed`
  once (set it true) and return to the poll loop; else Step 6-6.
- `PARKED` — worker already parked the epic; log and return to Step 1.

**Step 6-6 — Park for exhausted attempts:**
```bash
bd update <epic-id> --status blocked --add-label needs-human
bd update <epic-id> --notes "Auto-parked <ts>: ci-failed (exhausted after <ci_fix_attempts> attempt(s)) on epic PR #<pr-number>. Run: <run-id>."
bd comment <epic-id> "CI reconcile: parked after <ci_fix_attempts> fix attempt(s). Last run <run-id>. epic/<epic-id> + worktree preserved; children staged."
```
Log `PARKED: epic <epic-id> — ci-failed (exhausted)`; return to Step 1.

---

## Safety invariants

- **Workers never touch `main`.** They commit on `issue/<id>` off the epic branch; the
  orchestrator integrates into `epic/<id>` and opens the single epic PR.
- **One PR per epic.** Children merge into the epic branch with no per-child PR or CI;
  the epic ships once, gated by the `fast` check, full suite + deploy at the release tag.
- **Epics ship complete, never partial.** An epic flushes only when every child is
  `staged`. A parked/failed child blocks its whole epic — uniformly, curated or rollup —
  until a human clears it. Nothing partial reaches `main`.
- **Rollups seal, then ship — and only the human or the auto-seal seals them.** A rollup
  accepts loose one-offs until sealed (human applies label `sealed`, or auto at
  `ROLLUP_MAX_CHILDREN`). The orchestrator adds `sealed` in exactly one place: the Step 4
  auto-seal when the child count reaches the limit. It must not seal a rollup for any
  other reason — including because a batch of requested issues are all staged, or to make
  a flush happen. A sealed rollup is just a fixed-set epic; the label freezes membership,
  it does not ship anything early.
- **`bd close` only fires post-merge.** Children and the epic are batch-closed only after
  the epic PR's `state == MERGED` is confirmed.
- **Rebase before the PR.** The epic branch is rebased onto fresh `origin/main`
  immediately before its PR opens; a conflict parks the epic rather than landing stale.
- **Drain before diverging.** The loop finishes (ships or parks) the active epic before
  starting another, so an epic branch never falls behind `main` under serial work.
- **Flake reruns at most once; CI fix attempts bounded** (`MAX_CI_FIX_ATTEMPTS`).
  Worktrees and branches are preserved on any park.
- **The worst failure mode is idle**, not a broken `main`. A crash mid-loop leaves staged
  children on their epic branch and any armed epic PR open — nothing is lost.
