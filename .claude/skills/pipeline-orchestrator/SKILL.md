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
  version: "5.1.0"
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

---

## Epic authoring rules

These rules govern how children in a curated epic must be structured.

### `blocks` dependencies between batch siblings are supported

Sibling `blocks` deps are safe to use. The epic-aware ready check (Step 1) treats a
`staged` sibling as satisfying a dep — staged code is on `epic/<id>` and available for
dependent work to build on. The loop will naturally pick children in dependency order
without deadlocking.

**Priority is still the simpler choice for pure ordering.** If child B logically follows
child A but doesn't need A's code at compile time, a priority difference is less
bookkeeping than a dep:

```bash
# Option 1 — dep (correct if B genuinely needs A's code to build/pass):
bd dep add <child-B-id> <child-A-id>   # B is blocked by A

# Option 2 — priority (sufficient if the ordering is just logical preference):
bd update <child-A-id> --priority 1   # implement first
bd update <child-B-id> --priority 2   # implement after A is staged
```

Use a `blocks` dep when B would fail to build or test without A's code present. Use
priority when it's an ordering preference and both children are independently buildable.

### Validate a named batch before you build it (batch-closure gate)

When a human hands you a set to ship as one MR ("build A, B, C"), do **not** create the
epic until the set is **dependency-complete**: every open issue any member depends on must
also be in the set. (A blocker already on `main`/closed is fine — it's satisfied.) An
incomplete set silently deadlocks — a member whose blocker isn't in the batch can never
build, so the epic never reaches 100% and never ships.

Run the deterministic check before authoring the epic branch:

```bash
python .claude/skills/pipeline-orchestrator/check_batch.py <id> <id> <id>
```

- **`GATE: VALID`** (exit 0) — complete; it prints the dependency-ordered build order.
  Author the epic with exactly these children.
- **`GATE: INCOMPLETE`** (exit 2) — missing dependencies. **Stop and ask the human**,
  relaying the report's two directions verbatim — never pick for them, never silently
  expand the set; the batch boundary is the human's call:
  - **ADD** the missing deps (the report lists the full transitive set and flags any that
    are `needs-spec`/`parked` — those aren't buildable, so they must be specced first or
    the dependent dropped), **or**
  - **DROP** a dependent (the report shows the cascade — everything depending on it goes
    too).

  Re-run the check on the amended set until it is `VALID`.
- **`GATE: ERROR`** (exit 1) — none of the named ids are open (typo, or already shipped).

`blocks` is the only edge type that gates a build; `relates_to` is ignored. The check
reads one `bd graph --all --json` snapshot and does not mutate anything.

### Autonomous planning ("just go")

With no human-named set, `check_batch.py --plan` prints every open connected component as
a ranked candidate batch, marking each **DRAINABLE** (all members buildable — ship the
whole component in the printed order) or **BLOCKED** (contains a `needs-spec`/`parked`
node; only the buildable prefix can ship this round). The loop **never asks a question**
in this mode — it drains drainable components and skips blocked ones until a human
unblocks them.

### Sweep / cleanup children belong as post-epic follow-ups, not batch siblings

A "sweep" child (dead-code removal, final cleanup, doc update) that runs against the
**merged** code cannot be a batch sibling — it needs the code on `main`, not just staged
on the epic branch. File it as a follow-up issue after the epic PR merges, either
manually or via `bd create` in the post-merge batch-close step:

```bash
# After confirming MERGED (Step 5-5), before closing children:
bd create --title="<sweep task>" --description="Follow-up sweep after epic <epic-id> merged to main." \
  --type=task --priority=<p>
```

### Diagnosing a stalled epic during a manual run

If the loop appears stuck (epic has staged children but no further child is claimed):

```bash
bd show <epic-id>          # see all children and their status
bd blocked                 # see which children are blocked and by what
bd show <blocked-child-id> # inspect the blocker
```

A child blocked on a **staged** sibling should be picked up automatically by the
epic-aware ready check. If it isn't, confirm the orchestrator is running Step 1's local
ready logic (not falling through to `bd ready` alone for epic children). A child blocked
on a non-staged, non-closed sibling is genuinely waiting — the blocker must be staged
first.

---

## Invocation

```
/loop /pipeline-orchestrator      # self-paced loop
/pipeline-orchestrator            # single pass (testing)
```

---

## Run setup — ask the merge-authorization question ONCE, up front

**Before Step 0 of the first iteration — and before claiming, building, or staging
anything — ask the human whether this run may merge its epic PR to `main` without
review.** This is a per-run decision recorded as `merge_authorized` (true/false) and
reused unchanged by every flush for the rest of the run; never re-ask mid-run.

Ask with `AskUserQuestion` (single question, single-select):

> **"How should this run land its epic PR to `main`?"**
> - **Auto-merge (no review)** — arm `gh pr merge --auto --merge` at flush; the epic
>   lands the moment the `fast` check goes green. *(Recommended for a hands-free run.)*
> - **Open PR and stop for my review** — open the epic PR, hand back the URL, and leave
>   it un-armed for the human to merge.

Record the answer:
- **Auto-merge** → `merge_authorized = true`.
- **Open PR and stop** → `merge_authorized = false`.

**Why up front:** arming auto-merge on an agent-authored PR to public `main` requires
the human's explicit consent, and the permission classifier blocks it otherwise. Asking
at flush means the run has already spent the full build/test/stage cost before it
discovers it cannot land — the whole point of asking now is that a hands-free run never
stalls at the last step. Get the answer before any tokens are spent on the work.

> If the human's invoking message already states the intent unambiguously ("run the
> pipeline and merge it", "build these but let me review the PR"), take that as the
> answer and skip the prompt — but still record `merge_authorized` and state which way
> you read it. When in any doubt, ask; the default is **not** to merge without consent.

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
  progress (an `epic/<id>` branch with staged-but-unmerged children exists), use the
  **epic-aware ready check** to find the next child to work on:

  1. `bd show <epic-id>` — collect all children.
  2. Discard any child that is `staged`, `in_progress`, `closed`, or `blocked`.
  3. For each remaining (`OPEN`, untagged) child, inspect its deps via `bd show <child-id>`.
  4. A dep is **satisfied** if the blocker is `CLOSED` **or** (still `OPEN` AND carries the
     `staged` label) — staged code is already on `epic/<id>` and available to build on.
  5. A child is **locally ready** when all its deps are satisfied.
  6. Pick the highest-priority locally-ready child. If none, the epic has no actionable
     work right now — pick a ready issue from elsewhere (but do *not* start a new epic
     until the active one has shipped or parked).

  > **Why not rely solely on `bd ready` here?** `bd ready` clears a dep only when the
  > blocker is `CLOSED`. In the batch model siblings stay `OPEN` until the epic flushes,
  > so sibling `blocks` deps would never clear — causing a permanent deadlock. The
  > epic-aware check substitutes `staged` as the satisfaction signal within the epic,
  > resolving deps correctly without breaking the batch model.
  >
  > `bd ready` is still used for the non-epic path (loose one-offs with no active epic)
  > where the closed-only rule is correct.

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

3. **Guard mergeability, then arm auto-merge — only if `merge_authorized`** (the epic PR
   is gated by the `fast` check):
   ```bash
   gh pr view <pr-number> --json mergeStateStatus --jq '.mergeStateStatus'
   ```
   - `DIRTY` → park the epic `merge-conflict` (as in step 1's conflict block) and return to Step 1.
   - `BEHIND` → park the epic `merge-conflict:behind`; return to Step 1.
   - `CLEAN` / `UNSTABLE` / `HAS_HOOKS` / `BLOCKED` / `UNKNOWN`:
     - **`merge_authorized == true`** → arm auto-merge:
       ```bash
       gh pr merge <pr-number> --auto --merge
       ```
       If `gh pr merge` fails: already-merged → continue to step 4; else park `merge-failed:<err>`.
     - **`merge_authorized == false`** → do **not** arm. The epic is built, rebased, and
       its PR is open — hand back to the human here:
       ```bash
       bd update <epic-id> --notes "Flush ready <ts>: epic PR #<pr-number> open, un-armed (run not authorized to merge without review). Children staged; nothing closed."
       ```
       Log `HANDOFF: epic <epic-id> — PR #<pr-number> open for human review (not auto-merged)`,
       relay the PR URL to the human, and return to Step 1. **Do not `bd close` anything**
       — the batch-close (Step 5-5) fires only after a human merges and a later iteration
       (or the human) confirms `state == MERGED`. The staged children and open PR are the
       durable handoff; nothing is lost.

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

- **Merge authorization is settled up front, once.** The run asks — before any work —
  whether it may merge its epic PR to `main` without review, and records `merge_authorized`.
  A run that was not authorized opens the epic PR and hands it back for human review rather
  than arming auto-merge; it never asks again mid-run, and never merges without the recorded
  consent. This keeps a hands-free run from spending the full build cost only to stall at
  the flush when the merge permission is refused.
- **Workers never touch `main`.** They commit on `issue/<id>` off the epic branch; the
  orchestrator integrates into `epic/<id>` and opens the single epic PR.
- **One PR per epic.** Children merge into the epic branch with no per-child PR or CI;
  the epic ships once, gated by the `fast` check, full suite + deploy at the release tag.
- **Epics ship complete, never partial.** An epic flushes only when every child is
  `staged`. A parked/failed child blocks its whole epic — uniformly, curated or rollup —
  until a human clears it. Nothing partial reaches `main`.
- **Named batches are validated before they build.** A human-named batch must pass the
  batch-closure gate (`check_batch.py`) — every open dependency present in-set — before its
  epic is created. An incomplete set is refused with an add-or-drop decision, never
  silently expanded and never half-built. The autonomous loop drains only self-complete,
  buildable components (`--plan`).
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
- **Sibling deps are resolved by the epic-aware ready check, not `bd ready`.** `bd ready`
  only clears a dep when the blocker is CLOSED; the epic-aware check (Step 1) also
  accepts `staged` as satisfied within an epic. Sibling `blocks` deps work correctly;
  `bd ready` is used only for the non-epic (loose one-off) path. See "Epic authoring
  rules" for when to use deps vs. priority.
