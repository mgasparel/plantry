---
name: daily-briefing
description: >-
  Generate a unified daily operator report: ONE command, ONE tabbed HTML with
  [Briefing | Flow | Backlog | Trend] tabs. The Briefing tab shows factory stall
  (items stopped on me), overnight recap, burn-down + backlog health (net burn,
  days-to-backlog-zero, stale/untriaged signals), the priority queue, and the
  ranked "where to spend attention" call written in chat. The Flow tab shows
  lead time, throughput, aging WIP, and self-generated-work rate. The Backlog
  tab shows the full do-now / parked detail. The Trend tab charts each KPI
  metric across nightly snapshot dates from health-log.jsonl.
  USE FOR: "daily briefing", "morning report", "what should I do today",
  "run the briefing", "show me the briefing", "operator report",
  "morning standup summary", "what happened since yesterday",
  "triage the backlog", "show beads flow over time", "lead/cycle time",
  "throughput chart", "aging WIP", "is the autonomous loop net-positive",
  "health trend", "KPI over time", "how is the factory performing over time".
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
| **Briefing** | Factory stall, overnight recap, burn-down + backlog health, priority queue, the call |
| **Flow** | Lead-time scatter, throughput, aging WIP, self-generated work |
| **Backlog** | Ranked do-now / parked detail |
| **Trend** | KPI health-over-time charts from `health-log.jsonl` (nightly snapshots) |

Landing tab is **Briefing**.

## Usage

```powershell
# Generate to ./daily-briefing.html and open in browser
powershell -File "<skill-dir>/briefing.ps1" -Open

# Generate to a custom path
powershell -File "<skill-dir>/briefing.ps1" -Out C:\path\briefing.html

# Emit the full JSON data contract to stdout (for debugging / piping)
powershell -File "<skill-dir>/briefing.ps1" -Json

# Use a custom health-log path (default: <skill-dir>/health-log.jsonl)
powershell -File "<skill-dir>/briefing.ps1" -HealthLog C:\path\health-log.jsonl -Open
```

Resolve `<skill-dir>` as the directory containing this `SKILL.md`.

## Health log (Trend tab data source)

`health-log.jsonl` lives in the same directory as `briefing.ps1` and `SKILL.md`.
It is **git-tracked** so the trend accumulates across machines and branches.
Each run appends at most one row (one per calendar day). The file is NOT
gitignored -- commit it along with the generator to preserve trend history.

The generated `daily-briefing.html` is gitignored (it is a snapshot artifact);
the log that feeds the Trend tab is what gets committed.

Schema per row:
```json
{
  "date": "YYYY-MM-DD",
  "leadP50h": float,
  "leadP85h": float,
  "leadP95h": float,
  "throughputPerDay": float,
  "openCount": int,
  "reasonMix": { "total", "inflight", "blocked", "spec", "ready", "parked",
                 "pctInflight", "pctBlocked", "pctSpec", "pctReady", "pctParked" },
  "sgOutstanding": int,
  "netPerDay": float,
  "burnDownDays": float | null,
  "creationRate": float,
  "readyDepth": int,
  "consumptionRate": float,
  "oldestOpenAgeDays": float | null,
  "staleCount": int,
  "untriagedCount": int,
  "stallCount": int
}
```

Rows written before the burn-down redesign carry `runwayDays` instead of the
net-burn / health fields; readers must tolerate both shapes (charts null-gap
the missing prefix).

## Data contract (-Json payload)

```
{
  "generatedAt": "YYYY-MM-DD HH:mm",
  "trend": {
    "rows":      [ { ... see health-log schema above ... } ],  // sorted asc by date
    "rowCount":  int,
    "startDate": "YYYY-MM-DD" | null    // date of the first row ever logged
  },
  "briefing": {
    "kpis": {
      "netPerDay":           float,          // closes/day - creates/day, 14-day trailing
      "burnDownDays":        float | null,   // open / netPerDay; null unless net > 0
      "creationRate":        float,          // creates/day, 14-day trailing window
      "consumptionRate":     float,          // closes/day, 14-day trailing window
      "readyDepth":          int,
      "openNonEpic":         int,
      "trailingDays":        int,
      "staleDays":           int,            // staleness threshold (-StaleDays, default 30)
      "staleCount":          int,            // open items older than staleDays
      "oldestOpenId":        string | null,
      "oldestOpenTitle":     string | null,
      "oldestOpenAgeDays":   float | null,
      "untriagedCount":      int,            // open issues with no class: label
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
prints a one-line KPI summary (net burn, burn-down horizon, stall counts,
overnight delta, oldest/stale, triage gate, leak budget), and opens it with
`-Open`. If it fails or `bd` is unreachable, surface the error -- do not report
success without a written file.

### Step 2 -- Read the Briefing tab

Work through the Briefing tab top to bottom. Section order:

1. **Factory stall** (line stopped on me) -- items the autonomous loop cannot
   clear without human input. Unblock these first. Categories in priority order:
   needs-human > parked-exhausted > blocked > long-in-progress > red-CI.
2. **Overnight recap** -- KPI strip: stall count, net burn/day, burn-down
   horizon, oldest open item, untriaged count, created/closed in last 24h,
   blocked count.
3. **Burn-down + backlog health** -- the factory clears ready work nightly, so
   the goal is a SHRINKING backlog, not a full queue. Headline: net burn
   (closes/day - creates/day, 14-day trailing) and days-to-backlog-zero.
   Growth is informational, not an alarm. The warning-bearing signals are the
   health row: stale items (open > StaleDays, default 30) and untriaged count.
4. **Priority queue** -- quality leaks first (bugs + UX), then parked
   investments. The operator's job is ordering and grooming, not feeding.
   Gate warning if untriaged issues exist (run `groom` first).
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

Backlog: {open} open, net {net:+/-}/day ({shrinking ~{burnDown}d to zero | steady | growing}).
Stall: {N} item(s) stopped ({category breakdown if > 0}).
Health: oldest {age}d ({id}), {stale} stale, {untriaged} untriaged.
Overnight: +{created} / -{closed} (net {net:+/-}).

1. **{most urgent action}** -- {one line: why this first, cite the ID or number}
2. **{second action}** -- {one line}
3. **{third action (omit if nothing concrete)}** -- {one line}
```

Ranking logic (apply in order, stop when you have 3):
- Any needs-human stall item -> unblock it first (name the ID).
- Open P1 bugs / leak-budget items -> fix the highest-priority leak (name it).
- Untriaged issues (triage gate FAIL) -> run `groom` so the queue is trustworthy.
- Stale items (open > StaleDays) -> name the oldest ID; pull, spec, or close it.
- Backlog growing (net negative) several days running -> mention it as context,
  not as an action -- growth alone is not an alarm.
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
- **health-log.jsonl is git-tracked** -- this is the Trend tab's data source.
  Commit it alongside the generator to preserve the cross-session trend window.
  Each briefing run appends at most one row (idempotent per calendar day).
- **On-demand only.** No session-start hook; runs when invoked.
- **Supersedes:** `daily-report` (morning summary), `triage` (backlog ranking),
  `flow-report` (lead/throughput/aging/self-gen charts). Those skills are
  retired; their functionality is fully embedded in this briefing.
- **`groom` is still standalone.** Classifying / labelling issues is a separate
  concern; use `groom` to fix label drift before running the briefing if the
  triage gate warns about untriaged issues.
