# Claude Code instructions

## UI work

Plantry's UI is server-rendered Razor Pages with htmx and Alpine.js — no SPA or client bundler. Styles live in `src/Plantry.Web/wwwroot/css/plenish.css`.

`src/Plantry.Web/Pages/Dev/Index.cshtml` is the source of truth for shared, reusable UI: Razor tag helpers and partials, plus canonical CSS patterns reused across pages. It is not an inventory of every element in the app; page-specific layout and one-off feature compositions belong on their feature pages.

Before building a UI element:

1. Check the library and compose from an existing primitive when one fits.
2. Extract repeated markup into a shared primitive before its second independent copy.
3. Add only reusable primitives to the library. Keep single-use compositions on their feature page.

## Ticket delivery

For issue-driven delivery, read `AGENTS.md`, run `bd prime`, and use `.claude/skills/factory-run/SKILL.md`. The six factory skills and their independent role definitions are the current delivery source of truth. Record phase and candidate-bound evidence in Beads; resume an unfinished ticket before claiming another. Keep delivery serial at one ticket, branch, worktree, and PR at a time.

Use `.claude/review-criteria.md` as the canonical review gate. Test adequacy is based on protected behavior, meaningful assertions, relevant negative paths, and appropriate test level; do not introduce test-count or coverage-percentage targets.
