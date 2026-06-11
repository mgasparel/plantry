---
name: stryker-report
description: >-
  Parse and summarize a Stryker .NET mutation testing report. USE FOR:
  "stryker report", "mutation report", "mutation score", "mutation testing
  results", "parse stryker", "what did stryker find", "survived mutants",
  "mutation coverage". DO NOT USE FOR: running Stryker (use `stryker-run.ps1`
  or `dotnet stryker` directly), or deciding what to do about survivors (use
  the mutation-triage skill).
license: MIT
metadata:
  author: plantry
  version: "0.2.0"
---

# Stryker Mutation Report

Parse the Stryker mutation report JSON and present the score, per-file
breakdown, survivor detail, and config advisories. This skill is read-only
reporting — actioning survivors is the mutation-triage skill's job.

## Procedure

### Step 1 — Run the parser script

```powershell
# Auto-find latest report:
python .claude/skills/stryker-report/parse-report.py

# Or with a specific file:
python .claude/skills/stryker-report/parse-report.py <path-to-mutation-report.json>
```

If the script exits 1, relay the error to the user (they likely need to run
`.\stryker-run.ps1` first).

The script outputs: overall score with threshold status, a file table sorted
by survived count, survived-mutant detail per file, NoCoverage detail, and
config advisories (migration files, CompileErrors, Timeouts).

### Step 2 — Present the summary

Show the script output verbatim — do not restate the tables. Add a one-line
interpretation of the overall status, e.g. "76.5% — between the low (75%) and
high (90%) thresholds; 1 non-migration file has survived mutants."

If there are survivors, close with: "To triage these survivors, run
`/mutation-triage`."

## Notes

- Never re-run Stryker as part of this skill — only parse an existing report.
- If multiple `StrykerOutput/` timestamp directories exist, the script picks
  the newest automatically; pass `--file` to target a specific run.
- Migration files appear because Stryker doesn't exclude them by default; the
  config advisory tells the user how to fix this.
