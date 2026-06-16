---
name: triage
description: >-
  Rank the backlog and produce a do-now/parked report. USE FOR: "what should I
  work on next", "triage the backlog", "what's left for MVP". DO NOT USE FOR:
  classifying issues (that's `groom`) or implementation work.
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Triage

The goal isn't "what's most important" in the abstract — it's **drive toward
MVP while keeping quality leaks near zero**. That splits the backlog cleanly:

- **Quality leaks** — `class:bug` + `class:ux`. These erode the product even
  when the feature set is "done." Surface them, rank them, keep this pile small.
- **Quality investments** — `class:improvement` + `class:tech-debt`. Genuinely
  valuable, deliberately deferred to protect velocity. The job here is to *show
  they're safely filed* so deferring them doesn't feel like losing them.

**Themes** are the lens: instead of a flat list you see where the leaks cluster
vs. where the nice-to-haves pile up.

This skill trusts the `class:`/`theme:` labels — so its first duty is to make
sure they exist.

## Procedure

1. **Gate and load the backlog.** Run:
   ```bash
   python "<skill-dir>/prep.py"
   ```
   If the first line reads `GATE: FAIL`, stop and run the `groom` skill — tell
   the user "N issues are untriaged; grooming before I can report." Only proceed
   once it reads `GATE: OK`. The output gives you the full DO-NOW pool (bugs +
   ux) and PARKED pool (improvement + tech-debt) grouped by theme, the leak
   budget tally, and ready/blocked state with blocker ids per issue. Use it as
   your working dataset for steps 3–7; only fall back to `bd show <id>` when you
   need a full description to judge severity or a quick-win call.

2. **Build the DO-NOW list (quality leaks).** Take `class:bug` + `class:ux`,
   group by `theme:`, and rank within the whole list by:
   - **user impact / severity** — a wrong result or a dead-end stub outranks
     cosmetic roughness;
   - **startability** — a leak flagged `needs-spec` or `needs-split` is *not*
     "just go fix it." Surface it with a **"define first"** / **"split first"**
     note and keep it off the very top — the next action is grooming the issue,
     not coding it;
   - **ready vs blocked** — a blocked leak can't be started; note its blocker
     and rank it below ready work;
   - **unblock leverage** — a leak that also frees other issues ranks up;
   - **quick win** — a leak carrying `quick-win` (or one you judge small and
     contained from its description) ranks up; cheap leak-clearing is high value.
   Give each a one-line rationale tied to MVP/quality, not a generic restatement.

3. **Build the PARKED list (investments).** Take `class:improvement` +
   `class:tech-debt`, group by `theme:`. Don't rank these — just list them under
   their themes so they're visibly *filed, not forgotten*. Note any that are
   secretly blocking a leak (a refactor a bug fix depends on); call those out as
   "promote if you start <theme>."

4. **Tally the leak budget.** Read the `LEAK BUDGET` line from `prep.py`'s
   output — it's the number to drive toward zero for MVP, and is the headline.

5. **Present the report** (see format). Lead with the leak tally and the top 3–5
   do-now picks; keep the parked list scannable.

6. **Optionally normalize priorities (propose → apply on OK).** If bd priorities
   disagree with the triage (e.g. a `class:bug` sitting at P3, or a
   `class:improvement` at P2 jostling with leaks), propose a small batch of
   `bd update <id> --priority=<p>` changes as one table and apply only after the
   user approves. This is secondary — the report is the deliverable; skip it if
   priorities already line up.

## Report format

```markdown
# Triage — {date}

**Quality-leak budget: {N} open ({b} bugs · {u} UX) — drive to 0 for MVP**
Parked (on purpose): {i} improvements · {d} tech-debt

## Do now — quality leaks (ranked)
### theme:{name}  ({k} leaks)
1. plantry-xxx [bug] {title} — {why it matters for MVP/quality}  · {ready|blocked by yyy}  {· quick-win}
2. plantry-xxx [ux]  {title} — {…}  · ⚠ define first (needs-spec)
3. plantry-xxx [ux]  {title} — {…}  · ⚠ split first (needs-split)
...

## Parked on purpose — investments
### theme:{name}
- plantry-xxx [improvement] {title}
- plantry-xxx [tech-debt]   {title}   {· promote if you start theme:X}
...

## Proposed priority changes (if any)
- plantry-xxx  P3 → P1   (bug, currently underweighted)
[approve → applies bd update; else report stands alone]
```

## Notes

- **Labels are the source of truth here.** Triage does not re-judge whether
  something is a bug or an improvement — `groom` already did. If a classification
  looks wrong, don't quietly override it in the report; flag it and suggest a
  groom fix, so the data and the report never drift apart.
- **Blocked leaks still count toward the budget** — they're open quality debt —
  but they sort below ready work because you can't act on them yet. Always name
  the blocker so the path to starting them is visible.
- **Don't pad the do-now list.** If there are three leaks, recommend three.
  The product value is a short, honest pile of what's actually hurting quality,
  not a long to-do list.
- Triage's only writes are the optional, approved priority changes in step 7. It
  never adds/removes `class:`/`theme:` labels (that's `groom`) and never touches
  source.
