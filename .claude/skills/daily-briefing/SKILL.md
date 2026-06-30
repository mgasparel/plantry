---
name: daily-briefing
description: >-
  Generate a unified daily operator report: ONE command, ONE tabbed HTML with
  [Briefing | Flow | Backlog] tabs. The Briefing tab shows factory stall (items
  stopped on me), overnight recap, runway gauge, replenishment worklist, and the
  ranked "where to spend attention" call written in chat. The Flow tab shows lead
  time, throughput, aging WIP, and self-generated-work rate. The Backlog tab
  shows the full do-now / parked detail.
  USE FOR: "daily briefing", "morning report", "what should I do today",
  "run the briefing", "show me the briefing", "operator report",
  "morning standup summary", "what happened since yesterday",
  "triage the backlog", "show beads flow over time", "lead/cycle time",
  "throughput chart", "aging WIP", "is the autonomous loop net-positive".
  DO NOT USE FOR: classifying/labelling issues (use `groom`).
license: MIT
metadata:
  author: plantry
  version: "1.0.0"
---

# Daily Briefing

One command each morning: gathers ALL data slices once and renders ONE
self-contained multi-tab HTML operator report. Supersedes the former
`daily-report`, `triage`, and `flow-report` standalone skills -- all three
are now folded into this briefing.

## Tabs

| Tab | Content |
|-----|---------|
| **Briefing** | Factory stall, overnight recap, runway, replenishment worklist, the call |
| **Flow** | Lead-time scatter, throughput, aging WIP, self-generated work |
| **Backlog** | Ranked do-now / parked detail |

Landing tab is **Briefing**.

## Usage

```powershell
# Generate to ./daily-briefing.html and open in browser
powershell -File "<skill-dir>/briefing.ps1" -Open

# Generate to a custom path
powershell -File "<skill-dir>/briefing.ps1" -Out C:\path\briefing.html

# Emit the full JSON data contract to stdout (for debugging / piping)
powershell -File "<skill-dir>/briefing.ps1" -Json
```

Resolve `<skill-dir>` as the directory containing this `SKILL.md`.

## Data contract (-Json payload)

```
{
  "generatedAt": "YYYY-MM-DD HH:mm",
  "briefing": {
    "kpis": {
      "runwayDays":          float | null,   // ready / consumptionRate
      "readyDepth":          int,
      "consumptionRate":     float,          // closes/day, 14-day trailing window
      "trailingDays":        int,
      "stallCount":          int,            // all factory-stall categories
      "overnightCreated":    int,            // last 24h
      "overnightClosed":     int,
      "blockedCount":        int,
      "longInProgressHours": int,
      "ghAvailable":         bool
    },
    "factoryStall": {
      "count":           int,
      "ghWarning":       string | null,
      "needsHuman":      [{ "id", "title", "status", "labels", "category", "reason", "lever" }],
      "parkedExhausted": [{ ... }],
      "blocked":         [{ ... }],
      "longInProgress":  [{ ... }],
      "redCi":           [{ ... }],
      "all":             [{ ... }]
    },
    "parked": [{ "id", "title", "status" }]
  },
  "flow": {
    "kpis":        { ... },               // lead p50/85/95, perDay, openCount, stuckCount
    "percentiles": { "p50", "p85", "p95" },
    "lead":        [ closedIssueRows ],
    "throughput":  [ { day, count } ],
    "aging":       [ openIssueRows ],
    "selfGen":     { series, totalCreated, totalClosed, outstandingNow }
  },
  "backlog": {
    "ready":   [ readyIssues ],
    "blocked": [ blockedItems ]
  },
  "triage": {
    "gateOk":      bool,
    "totalOpen":   int,
    "untriaged":   [{ id, title }],
    "budget":      { open_leaks, bugs, ux, improvements, tech_debt },
    "leakGroups":  [ { theme, rows } ],
    "parkedGroups":[ { theme, rows } ]
  }
}
```

## Procedure

### Step 1 -- Generate the report (deterministic)

```powershell
powershell -File "<skill-dir>/briefing.ps1" -Open
```

The script reads all beads issues once, computes every slice, writes ONE HTML,
prints a one-line KPI summary (runway, stall counts, overnight delta, triage
gate, leak budget), and opens it with `-Open`. If it fails or `bd` is
unreachable, surface the error -- do not report success without a written file.

### Step 2 -- Read the Briefing tab

Work through the Briefing tab top to bottom. Section order:

1. **Factory stall** (line stopped on me) -- items the autonomous loop cannot
   clear without human input. Unblock these first. Categories in priority order:
   needs-human > parked-exhausted > blocked > long-in-progress > red-CI.
2. **Overnight recap** -- KPI strip: stall count, runway, ready depth,
   consumption rate, created/closed in last 24h, blocked count.
3. **Runway gauge** -- ready-queue depth / consumption rate = days until the
   factory starves if the operator does nothing. Thresholds: <3d urgent,
   3-7d warning, >=7d healthy.
4. **Replenishment worklist** -- quality leaks first (bugs + UX), then
   investments. Gate warning if untriaged issues exist (run `groom` first).
5. **The call** -- see Step 3.

If the user wants flow details, switch to the **Flow** tab.
If the user wants the full ranked backlog, switch to the **Backlog** tab.

### Step 3 -- Write the call in chat (model layer)

After opening the report, read the one-line KPI summary printed by the script
and write a ranked "where to spend attention" operator call directly in chat.
Do NOT re-query bd -- use what the script printed plus what is visible in the
HTML's Briefing tab.

Write 1-3 ranked actions (never more than 3). Use this structure:

```
**Attention call -- {date}**

Runway: {runway}d ({state: critical | low | healthy}).
Stall: {N} item(s) stopped ({category breakdown if > 0}).
Overnight: +{created} / -{closed} (net {net:+/-}).

1. **{most urgent action}** -- {one line: why this first, cite the ID or number}
2. **{second action}** -- {one line}
3. **{third action (omit if nothing concrete)}** -- {one line}
```

Ranking logic (apply in order, stop when you have 3):
- Any needs-human stall item -> unblock it first (name the ID).
- Runway < 3d -> replenishment is urgent; name the top leak or next item to promote.
- Open P1 bugs -> fix the highest-priority bug.
- Runway 3-7d -> watch the queue; name the next item to groom or promote.
- If all clear -> say so; one sentence is enough.

Do not recompute counts from scratch -- use what the script printed.

## Notes

- **ASCII-only generator.** The script stays ASCII (HTML entities in markup,
  no literal Unicode > U+007F) -- Windows PowerShell 5.1 reads a no-BOM UTF-8
  script as Windows-1252 and corrupts any literal em-dash / arrow / middot.
- **Single query.** All tabs share one `bd list --all` call; no per-tab
  re-querying. The JSON payload is the contract.
- **Generated HTML is gitignored** -- it is a data snapshot; only the generator
  is committed.
- **On-demand only.** No session-start hook; runs when invoked.
- **Supersedes:** `daily-report` (morning summary), `triage` (backlog ranking),
  `flow-report` (lead/throughput/aging/self-gen charts). Those skills are
  retired; their functionality is fully embedded in this briefing.
- **`groom` is still standalone.** Classifying / labelling issues is a separate
  concern; use `groom` to fix label drift before running the briefing if the
  triage gate warns about untriaged issues.
