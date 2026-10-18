# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:7510c1e2 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

<!-- END BEADS INTEGRATION -->

## Beads CLI guardrail: never pass free text inline on PowerShell

Never pass multi-line or quoted free text inline via `-d`/`--description` (PS 5.1
truncates at embedded quotes, silently — `CommandLineToArgvW` mis-rebuilds the
argument when it rebuilds the native command line, and bd stores only the text up
to the first embedded quote/whitespace boundary, with no error). Use
`--body-file <file>` / `--stdin` for `create` and `update`. For flags with no file
variant (`--notes`, `--acceptance`), use a Bash single-quoted heredoc, never
PowerShell inline.


## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT
complete until the feature branch is pushed and its PR has merged. If a gate blocks delivery, preserve the open PR and record the blocker without claiming completion.

> **Branch model:** nothing is pushed directly to `main`. Each ticket lands via
> one `issue/<id>` branch and one PR. The `factory-run` procedure owns the
> implementation, independent review, independent QA, CI, merge, and Beads
> lifecycle; do not use epic integration branches or batch delivery.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Persist factory state and evidence** - Record the phase and candidate-bound
   build, suite, review, QA, CI, and PR evidence in Beads. Resume an unfinished
   ticket before claiming new work.
4. **PUSH FEATURE BRANCH + OPEN ONE PR** - This is MANDATORY:
   ```bash
   git push -u origin issue/<id>   # push the feature branch (never main directly)
   gh pr create --title "<title>" --body "<description>" --base main
   ```
   Confirm with `gh pr view` that the PR is open and every required CI check has
   passed for the current candidate.
5. **Enable auto-merge only after all gates pass:**
   ```bash
   gh pr merge <pr-number> --auto --merge
   ```
6. **Confirm merge before closing** - Verify `gh pr view` reports MERGED, record
   the merged commit in Beads, and only then close the issue. Clean up only the
   worktree created for that ticket; retain the branch until merge confirmation.
7. **Verify and hand off** - Confirm the merged PR and push Beads data with
   `bd dolt push`; provide context for the next session if work remains.

**CRITICAL RULES:**
- Work is NOT complete until the feature branch is pushed and its PR is merged
- NEVER push directly to `main` — branch protection will reject it once enabled
- NEVER enable auto-merge before local verification, independent review, independent QA,
  and required CI pass on the same candidate
- NEVER delete the feature branch before the PR merges — CI needs it
- If `gh pr create` fails, resolve and retry; a local-only branch leaves work stranded
