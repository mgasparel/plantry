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
