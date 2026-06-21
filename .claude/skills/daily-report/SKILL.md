---
name: daily-report
description: >-
  Generate a morning activity summary (velocity, headline, bugs, code-review
  findings, backlog health, suggested next focus). USE FOR: "daily report",
  "morning standup summary", "what happened since yesterday", "show me today's
  activity report". DO NOT USE FOR: triaging the backlog (use `triage`) or
  classifying issues (use `groom`).
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Daily Report

Produces a concise at-a-glance morning summary: shipping velocity, the headline
work that dominated the window, open bugs, code-review auto-filed findings, and
a suggested next focus. On-demand only — invoke when the user asks; no auto-run.

## Procedure

### Step 1 — Gather the data (deterministic)

Run the prep script, passing `--Since` if the user specified a date:

```powershell
# Default window: since yesterday
powershell -File "<skill-dir>/report.ps1"

# Explicit window
powershell -File "<skill-dir>/report.ps1" -Since 2026-06-20
```

If the script is not found at `<skill-dir>/report.ps1`, resolve `<skill-dir>`
as the directory containing this `SKILL.md` file. The script emits a
pre-formatted payload; consume it as your working dataset for steps 2–6. Do
NOT run additional `bd` or `gh` commands — the payload has everything.

If the script fails or is unreachable, surface the error to the user before
continuing; do not silently produce an empty report.

### Step 2 — Detect the headline

Group the **beads_closed** rows by `parent` (epic id). The parent(s) with the
most closed children is the headline theme. If one epic had all its children
close in the window (check beads_created for the epic row itself or infer from
the payload), call it "fully landed." If there is no obvious dominant parent,
group by label `theme:` instead and call out the dominant theme.

Write 2–4 sentences: which epic/theme closed, which child PRs landed (use pr
numbers from prs_merged), and any other notable epics in flight.

### Step 3 — Suggested next focus

Reason over:
- `open_bugs` at P1 (highest priority quality leak)
- `cr_new` at P1 (freshest code-review regressions in the shipped epic)
- `untriaged` items (anything that needs a human decision before work can start)

Recommend 1–3 concrete next actions. Prefer "fix P1 bug X" over vague
"address quality debt." If a freshly landed epic introduced a P1 code-review
finding, call that out explicitly — it is the most actionable next step.

### Step 4 — Render the report

Use this format (mirror the 2026-06-21 sample):

```markdown
# Daily Activity Report — {date}    (window: since {since})

## Shipping velocity
| Metric | Count |
|--------|-------|
| Beads created / closed | {created} / {closed}  (net {net}) |
| PRs opened / merged / still open | {opened} / {merged} / {open} |

## Headline work — {epic name or theme}
{2–4 sentences from Step 2}

## Open bugs ({count})
| ID | P | Theme | Title |
|----|---|-------|-------|
| plantry-xxx | P1 | inventory | ... |
...

## Code-review auto-filed findings — {cr_new_count} new + {cr_outstanding_count} outstanding
**New this window:**
- plantry-xxx P1 [status] title

**All outstanding open:**
- plantry-xxx P1 [status] title
...
(Call out any P1 new findings explicitly.)

## Untriaged / needs-spec ({untriaged_count})
- plantry-xxx P? [status] title
...

## Backlog health
open: {open}   in_progress: {in_progress}   blocked: {blocked}   ready: {ready}

**Suggested next focus:**
1. {concrete action — bug id or task}
2. {concrete action}
3. {concrete action (optional)}
```

If the `gh` CLI was unavailable the payload will include a warning; surface it
in the report under the velocity section so the user knows PR data is missing.

### Step 5 — Offer to save (optional)

After rendering, offer: "Save this report as `.preflight/daily-{date}.md`?" and
apply only if the user says yes. Do not save automatically.

## Notes

- **Model layer is thin.** Only Steps 2 (headline detection) and 3 (next-focus
  reasoning) are genuinely non-deterministic. Everything countable came from the
  script — do not recompute counts from the payload lists; use the pre-computed
  `*_count` fields in the summary section.
- **`--Since` defaults to yesterday.** An explicit YYYY-MM-DD can be passed by
  the user, e.g. "daily report since Monday" → resolve the date → pass it.
- **On-demand only.** This skill has no session-start hook; it runs when invoked.
- **Pattern reference:** mirrors the `triage` skill (`SKILL.md` + `prep.py`):
  deterministic data in the script, judgment only in the model layer.
