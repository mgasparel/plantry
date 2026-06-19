# Agent Instructions

This project uses **bd** (beads) for issue tracking. Run `bd prime` for full workflow context.

> **Architecture in one line:** Issues live in a local Dolt database
> (`.beads/dolt/`); cross-machine sync uses `bd dolt push/pull` (a
> git-compatible protocol), stored under `refs/dolt/data` on your git
> remote — separate from `refs/heads/*` where your code lives.
> `.beads/issues.jsonl` is a passive export, not the wire protocol.
>
> See [SYNC_CONCEPTS.md](https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md)
> for the one-screen overview and anti-patterns (don't treat JSONL as the
> source of truth; don't `bd import` during normal operation; don't
> reach for third-party Dolt hosting before trying the default).

## Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work atomically
bd close <id>         # Complete work
bd dolt push          # Push beads data to remote
```

## Non-Interactive Shell Commands

**ALWAYS use non-interactive flags** with file operations to avoid hanging on confirmation prompts.

Shell commands like `cp`, `mv`, and `rm` may be aliased to include `-i` (interactive) mode on some systems, causing the agent to hang indefinitely waiting for y/n input.

**Use these forms instead:**
```bash
# Force overwrite without prompting
cp -f source dest           # NOT: cp source dest
mv -f source dest           # NOT: mv source dest
rm -f file                  # NOT: rm file

# For recursive operations
rm -rf directory            # NOT: rm -r directory
cp -rf source dest          # NOT: cp -r source dest
```

**Other commands that may prompt:**
- `scp` - use `-o BatchMode=yes` for non-interactive
- `ssh` - use `-o BatchMode=yes` to fail instead of prompting
- `apt-get` - use `-y` flag
- `brew` - use `HOMEBREW_NO_AUTO_UPDATE=1` env var

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

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT
complete until the feature branch is pushed and a PR is open (or the PR has merged).

> **Branch model:** nothing is pushed directly to `main`. All work lands via
> `issue/<id>` branches and PRs. The `implement-ticket-worker` agent pushes the
> branch and opens the PR as part of its normal flow; ad-hoc sessions follow the
> same pattern.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH FEATURE BRANCH + OPEN PR** - This is MANDATORY:
   ```bash
   git push -u origin issue/<id>   # push the feature branch (never main directly)
   gh pr create --title "<title>" --body "<description>" --base main
   ```
   Confirm with `gh pr view` that the PR is open. Work is not complete until
   the PR is open and the branch is on the remote.
5. **Enable auto-merge** (if branch protection is active):
   ```bash
   gh pr merge <pr-number> --auto --merge
   ```
6. **Clean up** - Clear stashes; do NOT delete the feature branch — it is pruned
   automatically post-merge by the pipeline orchestrator.
7. **Verify** - PR open on GitHub (`gh pr view`), beads data pushed (`bd dolt push`)
8. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until the feature branch is pushed and the PR is open
- NEVER push directly to `main` — branch protection will reject it once enabled
- NEVER delete the feature branch before the PR merges — CI needs it
- If `gh pr create` fails, resolve and retry; a local-only branch leaves work stranded
<!-- END BEADS INTEGRATION -->
