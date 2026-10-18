# Claude Code instructions

## UI work

Plantry's UI is server-rendered Razor Pages with htmx and Alpine.js — no Node, no bundler, no SPA. Styles live in `src/Plantry.Web/wwwroot/css/plenish.css`.

### Component library

`src/Plantry.Web/Pages/Dev/Index.cshtml` is the source of truth for **shared, reusable** UI — the cross-cutting building blocks feature pages compose with: reusable Razor tag helpers/partials (e.g. `<field>`) and canonical CSS patterns reused across pages (e.g. cards, segmented controls, pills, steppers). It is **not** an inventory of every element in the app — page-specific layout and one-off feature compositions live on their own feature page. It is development-only and 404s outside the `Development` environment.

Before building any UI element:

1. **Reuse first — check the library.** If a suitable primitive already exists, compose from its canonical markup; don't reinvent a near-duplicate. Reach for existing elements when designing new pages.
2. **Extract before you repeat.** If you'd be writing the same markup a second time, or building a clearly reusable widget, extract it into a tag helper / partial / CSS pattern, add it to the library first, then consume it from feature pages. Propose genuinely new reusable components to the user before building them.
3. **Don't catalogue page-specific markup.** A new library entry is warranted only when the thing is reused across pages or is a generic primitive. Single-use layout that merely composes existing primitives stays on its feature page.

## Ticket delivery

For issue-driven delivery, read `AGENTS.md`, run `bd prime`, and use `.claude/skills/factory-run/SKILL.md`. The six factory skills and their independent role definitions are the current delivery source of truth. Record phase and candidate-bound evidence in Beads; resume an unfinished ticket before claiming another. Keep delivery serial at one ticket, branch, worktree, and PR at a time.

Use `.claude/review-criteria.md` as the canonical review gate. Test adequacy is based on protected behavior, meaningful assertions, relevant negative paths, and appropriate test level; do not introduce test-count or coverage-percentage targets.
