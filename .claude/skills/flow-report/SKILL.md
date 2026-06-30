---
name: flow-report
description: >-
  Generate a visual HTML report of beads FLOW over time -- the dimension
  kanban/management views hide. Lead-time scatter (created->closed, with
  wait/exec split), throughput, reason-coded Aging WIP, and self-generated-work
  rate. USE FOR: "flow report", "show beads flow over time", "lead/cycle time",
  "throughput chart", "aging WIP", "is the autonomous loop net-positive". DO NOT
  USE FOR: a morning standup summary (use `daily-report`), prioritising the
  backlog (use `triage`), or classifying issues (use `groom`).
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Flow Report

Renders a self-contained HTML report of how work *flows* through beads over
time, not where cards sit right now. Four views:

1. **Lead-time scatter** (`created -> closed`, log y) with p50/85/95 lines;
   tooltip decomposes each issue into backlog **wait** vs. agent **exec**.
2. **Throughput** — closes per day (volume), gaps preserved.
3. **Aging WIP** — open non-epic issues by age, colour-coded by the **lever**
   each needs (in flight / blocked / needs-spec / ready / parked), with the
   p85/p95 lead thresholds as reference lines.
4. **Self-generated work** — `code-review` + `source:dogfood` issues filed vs.
   closed per day, with a cumulative outstanding line: is the loop net-positive?

On-demand only — invoke when the user asks; no auto-run.

## Procedure

### Step 1 — Generate and open the report (deterministic)

Run the generator. It reads the whole beads set, writes one HTML file, prints a
one-line summary, and (with `-Open`) opens it in the default browser:

```powershell
# Generate to the default ./flow-report.html and open it
powershell -File "<skill-dir>/flow-report.ps1" -Open

# Generate to an explicit path (no auto-open)
powershell -File "<skill-dir>/flow-report.ps1" -Out C:\path\flow-report.html
```

Resolve `<skill-dir>` as the directory containing this `SKILL.md`. The script
is fully self-contained (Chart.js from CDN, data embedded inline) and needs only
`bd` on PATH. If it fails or `bd` is unreachable, surface the error — do not
report success without a written file.

The script also accepts `-Json` to emit the computed payload to stdout instead
of HTML (for piping/debugging).

### Step 2 — Narrate the four signals (optional, thin)

The script prints `closed`, `open`, lead p50/85/95, and self-gen filed/closed.
If the user wants a read rather than just the file, give 2–4 sentences over
those numbers:

- **Lead time** — note p50 vs p95 spread; remember most lead is backlog *wait*,
  not build time (the median wait share is on the KPI strip). High wait is not
  automatically bad — in a triage-driven workflow much of it is intentional
  deferral.
- **Aging WIP** — read the reason mix, not just the count: how many need a spec,
  how many are blocked, how many are ready-but-unpulled. An aged *open* item is a
  decision overdue, not a stall.
- **Self-generated work** — is the outstanding line flat/falling (loop keeps
  pace) or climbing (generating faster than it clears)?

Do not recompute counts from scratch — use what the script printed / the KPI
strip in the report.

## Notes

- **Lead time, not cycle time, is the headline.** For agent-driven work,
  execution (`started -> closed`) is fast-by-design and uninformative; the signal
  lives in backlog dwell (`created -> started`). The report's methodology footer
  states this and its caveats (batch-creation, survivorship).
- **ASCII-only generator.** The script must stay ASCII (HTML entities in markup,
  plain ASCII in canvas tooltip strings) — Windows PowerShell 5.1 reads a no-BOM
  UTF-8 script as Windows-1252 and corrupts any literal `—`/`→`/`·`, which then
  ships as mojibake. Issue titles in the data are fine (decoded from `bd`).
- **Generated HTML is gitignored** — it's a data snapshot; only the generator is
  committed.
- **On-demand only.** No session-start hook; runs when invoked.
- **Pattern reference:** mirrors `daily-report` (`SKILL.md` + a deterministic
  PowerShell prep script); here the script also does the rendering.
