---
name: daily-briefing
description: >-
  Generate a unified daily operator report: ONE command, ONE tabbed HTML with
  [Briefing | Flow | Backlog] tabs. The Briefing tab shows factory stall (items
  stopped on me), overnight recap, runway gauge, replenishment worklist, and the
  call. The Flow tab is the full flow-report (lead time, throughput, aging WIP,
  self-gen). The Backlog tab shows the full do-now / parked detail.
  USE FOR: "daily briefing", "morning report", "what should I do today",
  "run the briefing", "show me the briefing", "operator report".
  DO NOT USE FOR: standalone flow metrics only (use flow-report), prioritising
  the backlog without opening a report (use triage).
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Daily Briefing

One command each morning: gathers ALL data slices once and renders ONE
self-contained multi-tab HTML operator report.

## Tabs

| Tab | Content | Status |
|-----|---------|--------|
| **Briefing** | Factory stall, overnight recap, runway, replenishment worklist, the call | Stall list + KPIs live; other sections land with DB-2 through DB-6 |
| **Flow** | Lead-time scatter, throughput, aging WIP, self-generated work | Full (reuses flow-report logic) |
| **Backlog** | Ranked do-now / parked detail | Placeholder -- lands with DB-5 (plantry-st0i3) |

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
      "runwayDays":       float | null,   // ready / consumptionRate
      "readyDepth":       int,
      "consumptionRate":  float,          // closes/day, 14-day trailing window
      "trailingDays":     int,
      "stallCount":       int,            // needs-human + needs-spec + needs-triage
      "overnightCreated": int,            // last 24h
      "overnightClosed":  int,
      "blockedCount":     int
    },
    "stallItems": [{ "id", "title", "status", "labels" }],
    "parked":     [{ "id", "title", "status" }]
  },
  "flow": {
    "kpis":        { ... },               // same as flow-report kpis
    "percentiles": { "p50", "p85", "p95" },
    "lead":        [ closedIssueRows ],
    "throughput":  [ { day, count } ],
    "aging":       [ openIssueRows ],
    "selfGen":     { series, totalCreated, totalClosed, outstandingNow }
  },
  "backlog": {
    "ready":   [ readyIssues ],
    "blocked": [ blockedItems ]
  }
}
```

## Procedure

### Step 1 -- Generate the report (deterministic)

```powershell
powershell -File "<skill-dir>/briefing.ps1" -Open
```

The script: reads all beads issues once, computes every slice, writes ONE HTML,
prints a one-line KPI summary, and opens it with `-Open`. If it fails or `bd`
is unreachable, surface the error -- do not report success without a written file.

### Step 2 -- Read the report; narrate if asked

Default landing is the **Briefing** tab. Work from top to bottom:

1. **Factory stall** -- unblock any needs-human items first.
2. **Runway** -- if runway < 3 days, replenishment is urgent.
3. **Overnight recap** -- what the loop did while you were away.
4. **Replenishment worklist** -- which issues to groom/spec/promote to ready.
5. **The call** -- the model layer writes this in chat (DB-6); until then, use
   the raw KPI numbers to form the recommendation.

If the user wants flow details, switch to the **Flow** tab.
If the user wants the full ranked backlog, switch to the **Backlog** tab.

### Step 3 -- Short narrative (optional)

If the user asks "what should I do today" after viewing the briefing, synthesise
in 3-5 sentences using the printed KPIs:

- Stall count first (if > 0, name the IDs).
- Runway number and whether it is comfortable or urgent.
- Overnight delta (created vs closed -- is the loop net-positive today?).
- One concrete next action.

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
- **Pattern reference:** mirrors `flow-report` (SKILL.md + a deterministic
  PowerShell prep script that also does the rendering).
