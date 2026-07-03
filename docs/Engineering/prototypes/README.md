# Design prototypes

Throwaway, self-contained HTML/markdown design artifacts cited by beads issues. They mirror
`plenish.css` tokens and real component markup but are **not wired to anything** — they exist to
pin a UX decision before implementation. Tracked here (rather than left in untracked `.preview/`
scratch) so the references survive development from an isolated git worktree. Safe to delete once
the owning epic closes.

| File | Owning work | What it settles |
|---|---|---|
| `quick-add-flyout-integration.html` | epic `plantry-ipuz` | In-place view-swap (search ⇄ create) inside the quick-add flyout; rejects the modal-off-flyout anti-pattern. |
| `quick-add-group-creation.html` | epic `plantry-ipuz` (follow-up to `plantry-8r7o`) | Creating a product **and** its group when neither exists yet. |
| `quick-add-variant-options.html` | epic `plantry-ipuz` (follow-up to `plantry-8r7o`) | Quick-add × variant flow design options. |
| `recipe-conversion-prompt-options.html` | conversion strand (`plantry-3mwx` + recipe-side prompt) | In-sheet "1 cup = ___ g" cross-dimension conversion prompt (Option 1). |
