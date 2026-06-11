# Mutation gate implementation plan

Goal: make `dotnet stryker --since mutation-checkpoint` a blocking step in
`plantry-preflight`. Runs are incremental (diff-only), so cost is proportional
to the PR, not the whole codebase.

## Steps (in order — each unblocks the next)

### 1. Fix runner output layout
**Why first:** everything downstream depends on knowing which report belongs to
which project.

In `stryker-run.ps1`, pass `--output StrykerOutput/<ProjectName>` to each
`dotnet stryker` call. Reports land at:
```
StrykerOutput/Plantry.Intake/<timestamp>/reports/mutation-report.json
```

### 2. Update parse-report.py for multi-project
With named subdirs, the script can find "latest report per project" without
reading every JSON. Update `find_latest_report` to accept an optional project
name; when called with no name, return a dict of `{project: path}` for all
projects. The script's output format gets a per-project header when running in
aggregate mode.

### 3. Update stryker-config.json
Change `since.target` from `"main"` to `"mutation-checkpoint"`. Leave
`enabled: false` — local full runs stay full; delta mode is opt-in via CLI.

```json
"since": {
  "enabled": false,
  "target": "mutation-checkpoint"
}
```

### 4. Add -Delta switch to stryker-run.ps1
When `-Delta` is passed, append `--since:enabled true` to each `dotnet stryker`
call. The target comes from config (`mutation-checkpoint`), so no extra arg
needed.

### 5. Seed the checkpoint tag
```bash
git tag -f mutation-checkpoint HEAD
git push origin mutation-checkpoint --force
```
Tag HEAD — existing survivors are considered triaged baseline. New code from
this point forward is held to the gate.

Advance the tag after each triage wave:
```bash
git tag -f mutation-checkpoint HEAD && git push origin mutation-checkpoint --force
```

### 6. Update plantry-preflight skill
Add a Stryker delta step between `dotnet test` and the code-review skill:

```
build → test → stryker delta → code-review
```

Step behaviour:
- Run `.\stryker-run.ps1 -Delta`
- If exit 0 → continue
- If exit 1 → run `parse-report.py` in aggregate mode, surface which projects
  have survivors, instruct the user to run `/mutation-triage` then re-run
  preflight. Stop — do not proceed to code-review.

## Out of scope here
- Nightly runs (dropped — incremental pre-flight solves the problem)
- Per-project threshold tuning (current `break: 60` applies to the delta)
