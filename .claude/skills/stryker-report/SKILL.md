---
name: stryker-report
description: >-
  Parse and action a Stryker .NET mutation testing report. USE FOR: "stryker
  report", "mutation report", "mutation score", "mutation testing results",
  "action mutation gaps", "parse stryker", "review mutations", "survived
  mutants", "mutation coverage", "what did stryker find". DO NOT USE FOR:
  running Stryker (use `stryker-run.ps1` or `dotnet stryker` directly) or
  general test coverage questions unrelated to a Stryker JSON report.
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Stryker Mutation Report

Parse the Stryker mutation report JSON, compute the mutation score, and produce
a prioritized action plan of test gaps to close — or file them as beads issues.

The companion script `parse-report.py` (in this skill directory) does all the
mechanical work. Claude's job is to interpret the output, read source context
for survived mutants, and write concrete test suggestions.

## Invocation Forms

- **`/stryker-report`** — run the script, print the full summary
- **`/stryker-report --file <path>`** — use a specific `mutation-report.json`
- **`/stryker-report --action`** — summary + per-file test suggestions (reads source)
- **`/stryker-report --issues`** — create beads issues for each file with survived mutants
- **`/stryker-report --action --issues`** — do both

## Procedure

### Step 1 — Run the parser script

```powershell
# Auto-find latest report:
python .claude/skills/stryker-report/parse-report.py

# Or with a specific file:
python .claude/skills/stryker-report/parse-report.py <path-to-mutation-report.json>
```

If the script exits 1, it will print the error — stop and relay it to the user
(they likely need to run `.\stryker-run.ps1` first).

The script outputs:
- **Header**: which report was loaded and when
- **Overall score** with threshold status (ABOVE HIGH / ABOVE LOW / BELOW LOW)
- **File table**: all files sorted by survived count descending, with per-status
  counts and score; migration files are flagged `(migration — skip)`
- **Survived mutant detail**: for each non-migration file with survived > 0,
  grouped mutants with line:col, mutator type, replacement, and coverage note
- **NoCoverage detail**: files with uncovered lines listed by line number
- **Config advisories**: migration files being mutated, CompileErrors, Timeouts

### Step 2 — Present the summary

Show the script output to the user verbatim as the summary. Do not restate or
paraphrase the file table — it's already formatted correctly.

Then add a one-line interpretation of the overall status, e.g.:
- "76.5% — between the low (75%) and high (90%) thresholds; 1 non-migration
  file has survived mutants."
- "54.4% — below the low threshold (75%); 12 files need test coverage."

### Step 3 — Action plan (when `--action` or `--issues`)

For each file listed under "Survived mutants by file" in the script output,
sorted as given (already by survived count descending):

1. **Read the source file** — use the `Read` tool. Read only the region around
   the survived mutants: aim for ±15 lines around each mutant's line, grouping
   nearby mutants into one read.

2. For each survived mutant (or group of related mutants on the same method),
   emit a suggestion block:

   ```
   **`src/Plantry.Pricing/Domain/PriceSource.cs` — L17 String mutation**
   Original: `"retail"` → `""`
   Gap: No test verifies that `PriceSource.Label` is non-empty. A mutation
        that replaces the string returns an empty label undetected.
   Fix: Assert `priceSource.Label` is not null or empty in existing
        `PriceSourceTests`, or add a new fact that covers this property.
   ```

3. **Prioritization within a file:**
   - Lead with `Equality mutation`, `Conditional boundary`, `Statement deletion`
     — these catch real logic bugs.
   - `String mutation` on display/format strings is lower priority; group them
     under a single suggestion per method rather than one per mutant.
   - If multiple mutants are in the same method body, write one consolidated
     suggestion naming the method and all the conditions it doesn't verify.

4. Skip files marked `(migration — skip)` — note once that they appear in the
   report and recommend the Stryker exclusion from the advisories section.

### Step 4 — Create beads issues (when `--issues`)

For each non-migration file with survived > 0, create **one** beads issue:

```bash
bd create \
  --title="Kill survived mutants in <ShortFileName>" \
  --description="<n> mutants survived in <relative-path>. Score: <score>%.

## Gaps
<bullet list — same content as the Step 3 suggestions for this file>

## Why
Survived mutants mean no test verifies this behavior. Each is a potential
undetected regression." \
  --type=task \
  --priority=<2 if score is below the low threshold, 3 otherwise>
```

After all issues are created, print a summary:

```
Created <n> issues:
  beads-NNN  Kill survived mutants in PriceSource.cs  (1 survived, 67%)
  ...
```

## Notes

- Never re-run Stryker as part of this skill — only parse an existing report.
- If multiple `StrykerOutput/` timestamp directories exist, the script picks the
  newest one automatically. Pass `--file` to target a specific run.
- Migration files appear in the report because Stryker doesn't exclude them by
  default. The config advisory tells the user how to fix this.
