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

**Why batch (v5):** after the CI cost change, per-PR CI is a fast
gate only and the full suite + deploy moved to the release tag. The remaining lever
is the *number* of PRs. So every issue flows through an **epic integration branch**:
children merge into `epic/<epic-id>` with no per-child PR and no per-child CI, and the
epic ships as **one** `epic→main` PR when it is 100% complete. A 10-child epic that
used to cost 10 PRs/merges/CI-runs now costs one.

**Serial model:** one issue at a time, and **drain the current epic before claiming
unrelated work** so the epic branch never falls behind `main`. There is no merge queue
(unavailable on this personal-account repo; deferred until org-owned — ADR-016). Two
things keep `main` safe: (1) the epic branch is **rebased onto fresh `origin/main`
immediately before its PR is opened** (the safety net for a concurrent human merge);
(2) the mergeability guard parks a behind/conflicting epic PR rather than landing it
stale.

**Two kinds of epic:**

| | Curated epic | Rollup epic (catch-all) |
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

### Scope locks must declare their kind and cite-or-create their companion bead

When authoring or amending a child spec that forbids work inside its own footprint ("do
not deduplicate here", "reused verbatim", "do not consolidate"):

1. **Declare the lock's kind** in the spec: `load-bearing` (protects a safety argument,
   e.g. a byte-identity move) or `hygiene` (mere tidiness). Hygiene locks are
   discouraged — prefer letting the worker do a small adjacent tidy as a separate commit.
2. **Cite or create the companion bead in the same spec.** A lock that defers known work
   names the bead that work lives in, creating it right then if needed. Otherwise the
   critic re-discovers the locked work later and the arbiter has nothing to ABSORB it
   into — the deferral gets tracked twice.

See "Spec scope locks" in `.claude/review-criteria.md` for how critics and the arbiter
treat locks.

### Sweep / cleanup children belong as post-epic follow-ups, not batch siblings

A "sweep" child (dead-code removal, final cleanup, doc update) that runs against the
**merged** code cannot be a batch sibling — it needs the code on `main`, not just staged
on the epic branch. File it as a follow-up issue after the epic PR merges, either
manually or via `bd create` in the post-merge batch-close step:

Never pass the description inline via `--description` on PowerShell (code/CLAUDE.md's bd
CLI guardrail) — write it to a scratch file first, then pass via `--body-file`. Here and
throughout this file, `<scratchpad>/<name>.md` means a path in the session scratchpad
**outside** any git worktree — never inside `../worktrees/<issue-id>` or
`../worktrees/<epic-id>`, since a stray file left in either tree risks being picked up
by a later commit, push, or merge of that branch:

```bash
# After confirming MERGED (Step 5-5), before closing children:
# write "Follow-up sweep after epic <epic-id> merged to main." to
# <scratchpad>/sweep-description.md
bd create --title="<sweep task>" --body-file "<scratchpad>/sweep-description.md" \
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

### Step 0 — Startup environment probe

Before claiming, dispatching, building, or staging any issue, capture the Docker probe
result and stop on failure:

```bash
scratchpad="<scratchpad>"
probe_file="$scratchpad/docker-unavailable.md"
mkdir -p "$scratchpad"
if ! probe_output=$(python .claude/skills/pipeline-orchestrator/docker_probe.py 2>&1); then
  printf '%s\n' "$probe_output" > "$probe_file"
  # No issue is claimed at this point. If an active epic exists, park it and preserve
  # the exact diagnostic; otherwise park this iteration without claiming work.
  if [ -n "${epic_id:-}" ]; then
    bd update "$epic_id" --status blocked --add-label needs-human
    bd update "$epic_id" --notes "$(cat <<'EOF'
unrecoverable-error:docker-unavailable; worker not dispatched
EOF
)"
    bd comment "$epic_id" --file "$probe_file"
  fi
  exit 1
fi
printf '%s\n' "$probe_output"
```

The probe checks both the Docker CLI and daemon server version. Exit 0 means usable;
any non-zero exit is an environment failure. Do not retry build/test: Docker failures
are distinct from code or zero-test failures. Once the probe succeeds, continue with
housekeeping and the claim workflow.

### Step 1 — Housekeeping

Prune stale registrations before claiming new work. Only touches already-merged or
abandoned state — never an in-progress branch or an active epic.

```bash
git worktree prune
git fetch origin --quiet
git branch --merged origin/main | grep -E '^\s+(issue|epic)/' | xargs -r git branch -d
```

`git branch -d` removes only branches already merged into `origin/main`.

### Step 2 — Claim one ready issue, guard Docker, and resolve its epic

After claiming an issue and resolving its parent epic, run the Docker probe again before
dispatching the worker. If it fails, persist the exact diagnostic before changing beads:

```bash
scratchpad="<scratchpad>"
probe_file="$scratchpad/docker-unavailable.md"
mkdir -p "$scratchpad"
if ! probe_output=$(python .claude/skills/pipeline-orchestrator/docker_probe.py 2>&1); then
  printf '%s\n' "$probe_output" > "$probe_file"

  bd update "$issue_id" --status blocked --add-label needs-human
  bd update "$issue_id" --notes "$(cat <<'EOF'
unrecoverable-error:docker-unavailable; worker not dispatched
EOF
)"

  if [ -n "${epic_id:-}" ]; then
    bd update "$epic_id" --status blocked --add-label needs-human
    bd update "$epic_id" --notes "$(cat <<'EOF'
unrecoverable-error:docker-unavailable; child worker not dispatched
EOF
)"
  fi

  bd comment "$issue_id" --file "$probe_file"
  if [ -n "${epic_id:-}" ]; then
    bd comment "$epic_id" --file "$probe_file"
  fi
  exit 1
fi
printf '%s\n' "$probe_output"
```

The failed post-claim transition is mandatory: the issue must leave `in_progress`, gain
`needs-human`, and retain the exact diagnostic. If an active epic owns the issue, also
block that epic with the same reason and diagnostic. Never dispatch while either probe
fails.
### Step 3 — Dispatch implement-ticket-worker (own its critic loop)

```
worker = Agent(subagent_type="implement-ticket-worker", prompt="<issue-id>")
```

The worker derives its base from the issue's parent (`epic/<epic-id>`), branches its
child worktree off it, pre-flights locally, commits, and returns — **without** pushing
or opening a PR.

**A dispatched subagent cannot spawn a further subagent of its own** — it has no access
to the `Agent` tool. The worker's Opus critic pass therefore cannot happen inside the
worker; you (the orchestrator, running at the top level) own that dispatch instead. Loop
on the worker's response:

```
loop:
  response = wait for worker

  if response is "=== implement-ticket READY-FOR-CRITIC ===" (fields ISSUE, WORKTREE,
     BASE, TESTS, PASS_COUNT):
    critic = Agent(model="opus", prompt=<the critic prompt template from
      implement-ticket-worker.md Step 4c, filled in with this handoff's ISSUE,
      WORKTREE, BASE, TESTS>)
    SendMessage(to=worker, "<critic's raw verdict text, verbatim>")
    continue loop

  if response is "=== implement-ticket READY-FOR-ARBITER ===" (fields ISSUE, WORKTREE,
     BASE, DEFER FINDINGS):
    arbiter = Agent(subagent_type="fable-arbiter", prompt=<"DEFER ruling for <ISSUE>.
      Worktree: <WORKTREE>. Base: <BASE>. Findings:\n<DEFER FINDINGS verbatim>">)
    SendMessage(to=worker, "<arbiter's raw ruling text, verbatim>")
    continue loop

  if response is "=== implement-ticket VERDICT ===" (RESULT: PASS | FAILED):
    done — fall through to Step 3
```

Each `READY-FOR-CRITIC` handoff gets a **fresh** Opus critic spawn — never reuse a critic
across passes, and never let the orchestrator itself read the diff or apply review
criteria; it only ferries the critic's verdict text back to the worker, which owns all
report-writing, `bd comment` posting, and the FIX-apply loop. The same firewall applies to
the arbiter handoff: spawn `fable-arbiter` fresh, ferry its ruling text back verbatim, and
never rule on findings yourself — the worker executes the rulings (FIX-IN-CASE applies /
FILE creates the bead / ABSORB comments an existing bead / DROP records the rationale).
This keeps the context-firewall property the worker dispatch already had — the
orchestrator's own context only ever holds a verdict's or ruling's worth of text, never
the implementation diff.

The final verdict looks like:

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

### Step 4 — Integrate the child (per verdict)

**On `RESULT: FAILED`:** the worker already parked the issue (`blocked` + `needs-human`).
What happens next depends on the park reason:

- **Capability-shaped reasons** (`critic-loop-exhausted`, `build-loop-exhausted`,
  `test-loop-exhausted`) → consult the arbiter before accepting the park:
  ```
  arbiter = Agent(subagent_type="fable-arbiter", prompt=<"Park ruling for <issue-id>.
    Reason: <REASON>. Report: <PREFLIGHT>. Worktree: <WORKTREE>. Branch: <BRANCH>.">)
  ```
  Then execute its ruling:
  - **PARK-FOR-HUMAN** → post the arbiter's COMMENT on the issue, log
    `PARKED: <issue-id> — <REASON>` and go to Step 4 (today's behaviour, now
    arbiter-confirmed).
  - **RETRY-ESCALATED** → post the COMMENT, then un-park the bead and clear the parked
    attempt so the retry starts genuinely clean — the arbiter has already read it:
    `--notes` has no file variant, so never pass it inline on PowerShell — build it via a
    Bash single-quoted heredoc (code/CLAUDE.md's bd CLI guardrail):
    ```bash
    bd update <issue-id> --status in_progress
    bd update <issue-id> --remove-label needs-human
    bd update <issue-id> --notes "$(cat <<'EOF'
Un-parked <timestamp>: arbiter ruled RETRY-ESCALATED on <reason-string>.
EOF
)"
    git worktree remove ../worktrees/<issue-id> --force
    git branch -m issue/<issue-id> issue/<issue-id>-parked-1   # rename, don't delete — the parked HEAD stays recoverable
    ```
    Then dispatch **one** fresh worker on Fable with the arbiter's distilled failure
    summary appended to the issue-id prompt:
    `Agent(subagent_type="implement-ticket-worker", model="fable",
    prompt="<issue-id>\n\nESCALATED RETRY (one attempt only). Prior-attempt summary from
    the arbiter:\n<distilled summary>")`. The retry worker's Step 2 now creates a fresh
    worktree and branch with no trace of the stuck attempt's tree. Run the same Step 2
    loop for the retry. If the retry also returns FAILED, park unconditionally — never
    consult the arbiter for a RETRY-ESCALATED ruling twice on the same issue (its
    guardrail; enforce it here too).
  - **OVERRIDE** → the arbiter has proven the final critic's blocking finding wrong (its
    ruling contains the enumeration + verification evidence). Post the COMMENT with that
    evidence, un-park the bead:
    `--notes` has no file variant, so never pass it inline on PowerShell — build it via a
    Bash single-quoted heredoc (code/CLAUDE.md's bd CLI guardrail):
    ```bash
    bd update <issue-id> --status in_progress
    bd update <issue-id> --remove-label needs-human
    bd update <issue-id> --notes "$(cat <<'EOF'
Un-parked <timestamp>: arbiter ruled OVERRIDE on <reason-string>; evidence in comments.
EOF
)"
    ```
    and resume the worker: `SendMessage(to=worker, "ARBITER OVERRIDE — the final verdict
    is treated as PASS. Proceed from Step 5 (squash, commit, completion comment, verdict).
    In the commit body and completion comment, state 'arbiter override — evidence in case
    comment' instead of claiming an Opus-review PASS. <arbiter's ruling text>")`. Then
    handle its PASS verdict normally.
- **All other reasons** (`underspecified-scope`, `blocked-on-dependency`,
  `unrecoverable-error:*`, merge conflicts) are decision- or environment-shaped — the
  arbiter is not consulted. Log `PARKED: <issue-id> — <REASON>` and go to Step 4.

Either way a still-parked child blocks its batch — the epic cannot reach 100% and will
not flush until a human (or a successful retry/override) clears it.

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

### Step 5 — Flush check

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

### Step 6 — Flush: one epic→main PR

Operate in the epic worktree `../worktrees/<epic-id>`.

1. **Rebase onto fresh `origin/main` (the safety net):**
   ```bash
   git fetch origin main --quiet
   git -C ../worktrees/<epic-id> rebase origin/main
   ```
   - Conflict → abort (`git -C ../worktrees/<epic-id> rebase --abort`), park the **epic**.
     Neither flag takes free text inline on PowerShell (code/CLAUDE.md's bd CLI
     guardrail): `--notes` has no file variant, so build it via a Bash single-quoted
     heredoc; `bd comment` does have a file variant, so write the detail to a scratch
     file first:
     ```bash
     bd update <epic-id> --status blocked --add-label needs-human
     bd update <epic-id> --notes "$(cat <<'EOF'
Auto-parked <ts>: epic-rebase-conflict on flush. Branch epic/<epic-id> + worktree preserved.
EOF
)"
     # write "Flush blocked: epic/<epic-id> conflicts with origin/main on rebase. A human
     # must rebase. Children remain staged; nothing closed." to
     # <scratchpad>/flush-blocked.md, then:
     bd comment <epic-id> --file <scratchpad>/flush-blocked.md
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
       `--notes` has no file variant, so never pass it inline on PowerShell — build it via
       a Bash single-quoted heredoc (code/CLAUDE.md's bd CLI guardrail):
       ```bash
       bd update <epic-id> --notes "$(cat <<'EOF'
Flush ready <ts>: epic PR #<pr-number> open, un-armed (run not authorized to merge without review). Children staged; nothing closed.
EOF
)"
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

5. **Batch-close on confirmed merge:** `--notes` has no file variant, so never pass it
   inline on PowerShell — build it via a Bash single-quoted heredoc (code/CLAUDE.md's bd
   CLI guardrail):
   ```bash
   # Close every child of the epic, then the epic.
   for child in <all child ids from bd show <epic-id>>; do
     bd update "$child" --notes "$(cat <<'EOF'
Merged to main in epic PR #<pr-number> (epic <epic-id>).
EOF
)"
     bd update "$child" --remove-label staged
     bd close "$child"
   done
   bd update <epic-id> --notes "$(cat <<'EOF'
Shipped via epic PR #<pr-number>. <n> children landed on main.
EOF
)"
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

### Step 7 — CI reconcile loop (epic PR)

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

**Step 6-4 — Park for env/config:** neither flag takes free text inline on PowerShell
(code/CLAUDE.md's bd CLI guardrail): `--notes` has no file variant, so build it via a
Bash single-quoted heredoc; `bd comment` does have a file variant, so write the log
excerpt to a scratch file first.
```bash
bd update <epic-id> --status blocked --add-label needs-human
bd update <epic-id> --notes "$(cat <<'EOF'
Auto-parked <ts>: ci-failed (env/config) on epic PR #<pr-number>. Run: <run-id>.
EOF
)"
# write "CI reconcile: epic PR #<pr-number> failed with env/config error (run <run-id>)
# — not a code fix. Log excerpt: <first 20 lines>. epic/<epic-id> + worktree preserved;
# children remain staged." to <scratchpad>/ci-reconcile-envconfig.md, then:
bd comment <epic-id> --file <scratchpad>/ci-reconcile-envconfig.md
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

**Step 6-6 — Park for exhausted attempts:** neither flag takes free text inline on
PowerShell (code/CLAUDE.md's bd CLI guardrail): `--notes` has no file variant, so build
it via a Bash single-quoted heredoc; `bd comment` does have a file variant, so write the
detail to a scratch file first.
```bash
bd update <epic-id> --status blocked --add-label needs-human
bd update <epic-id> --notes "$(cat <<'EOF'
Auto-parked <ts>: ci-failed (exhausted after <ci_fix_attempts> attempt(s)) on epic PR #<pr-number>. Run: <run-id>.
EOF
)"
# write "CI reconcile: parked after <ci_fix_attempts> fix attempt(s). Last run <run-id>.
# epic/<epic-id> + worktree preserved; children staged." to
# <scratchpad>/ci-reconcile-exhausted.md, then:
bd comment <epic-id> --file <scratchpad>/ci-reconcile-exhausted.md
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
