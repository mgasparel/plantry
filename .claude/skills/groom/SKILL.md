---
name: groom
description: >-
  Classify untriaged issues and fix label drift. USE FOR: "groom the backlog",
  "clean up labels", "the backlog data is messy". DO NOT USE FOR: prioritising
  work (that's `triage`) or any code changes.
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Groom

A backlog you can't trust can't be prioritized. `triage` only works if every
issue carries an honest **type** and a **`class:`** label; this skill is what
makes that true. It classifies the *untriaged* tail of the backlog and fixes
label drift, so the downstream report reads clean data instead of guessing.

It **never** decides what to work on or touches code — it only corrects issue
metadata in bd, and only after you approve the batch.

## The vocabulary

Four **orthogonal** axes plus the native type field. The whole point of keeping
them separate is that an issue can be a bug *and* under-specified *and* a
quick-win at once — collapsing those into one label loses signal. The `class:`
axis alone carries the do-now-vs-park signal.

### Axis 1 — `class:` (quality; exactly one per issue)

This is the label `triage` keys off.

| Label | Means | Test to apply |
|---|---|---|
| `class:bug` | Behaves incorrectly vs. intended. A quality *leak*. | "Is something broken / wrong?" |
| `class:ux` | Works, but the experience is rough, confusing, or a noticeable stub/gap a user would feel. A quality *leak*. | "Would a user hit this and wince, even though nothing 'errored'?" |
| `class:improvement` | Net-new capability or enhancement; its absence is not a defect. A quality *investment*. | "Is this a nice-to-have that doesn't block shipping?" |
| `class:tech-debt` | Internal code health — refactor, dead code, complexity, test/tooling — with no user-facing behaviour change. | "Is this purely about the code, invisible to users?" |

`class:bug` and `class:ux` are the **do-now** pile (quality leaks).
`class:improvement` and `class:tech-debt` are the **park** pile. Keep the leak
pile small.

### Axis 2 — `theme:` (area lens; one, occasionally two)

A coherent area of the product or codebase, e.g. `theme:shopping`,
`theme:recipes`, `theme:inventory`, `theme:intake`, `theme:tags`,
`theme:tech-debt`, `theme:ui-components`, `theme:home`. **Reuse before minting:**
run `bd label list-all` and prefer an existing `theme:` label; only create a new
one when nothing fits, and keep the name short and lowercase-kebab.

### Axis 3 — readiness (orthogonal flags; apply if true, any class can carry them)

These say "not ready to *start* as-is," independent of class. Triage uses them
to keep an unstartable leak off the top of the do-now list.

| Flag | Means | Mirror |
|---|---|---|
| `needs-spec` | **Under**-defined — you don't know what "done" looks like; define before starting. (Absorbs the old `stub` concept.) | the under-scoped end |
| `needs-split` | **Over**-scoped — really several issues under one ID, or open-ended ("investigate and prune…"); break up before starting. | the over-scoped end |

### Axis 4 — effort (flag)

| Flag | Means |
|---|---|
| `quick-win` | Small, contained, cheap to clear — one file / one obvious change, clear definition of done. Triage ranks these up. Apply sparingly; if you're unsure it's small, leave it off. |

### Native `type` field

Correct it while you're here: `bug` for `class:bug`; `class:tech-debt` is almost
always `task`; `ux`/`improvement` are `feature` (net behaviour) or `task` (small
change), by judgement.

### Provenance labels (leave alone)

`code-review` records *where an issue came from* (a critic/review finding), not
its triage class — it's a separate axis, kept as-is and informational. Do not
remove it. Same goes for `needs-human`

## Procedure

1. **Find the untriaged set and survey labels.** Run:
   ```bash
   python "<skill-dir>/prep.py"
   ```
   If it prints `GATE: CLEAN`, stop — the backlog is fully triaged. Otherwise
   it gives you: a compact row per untriaged issue (id, type, current labels,
   title); the existing `theme:` labels to reuse before minting new ones; and
   any stale zero-count labels worth deleting. Use this as your starting dataset
   for step 2; only run `bd show <id>` when you need the full description to
   classify an issue.

2. **Classify each untriaged issue.** For each, `bd show <id>` and read the
   title + description (don't classify from the title alone — `[stub]` in a
   title is a hint, not a verdict). Decide:
   - the `class:` label, using the Axis-1 table;
   - the `theme:` label, reusing existing ones;
   - readiness flags — `needs-spec` if under-defined, `needs-split` if
     over-scoped/compound (either can apply; neither is required);
   - `quick-win` only if it's genuinely small and contained;
   - whether the native `type` is wrong and what it should be.
   Note a one-line rationale per issue. If genuinely torn on the `class:`, pick
   your best guess but mark it `?` and add `needs-human` so it surfaces for
   review rather than hiding behind a confident-looking stamp.

3. **Propose the whole batch as one table** — do not write anything yet:

   ```
   PROPOSED GROOMING (n issues)

   id            type            +class:            +flags                +theme:             why
   plantry-6vg   bug (ok)        class:bug          —                     theme:recipes       fulfillment recomputes wrong on scale-up
   plantry-04j   task (ok)       class:tech-debt    —                     theme:ui-components Dev-library component parity, invisible to users
   plantry-gta   feature (ok)    class:ux           needs-spec            theme:home          empty landing stub a user lands on; under-defined
   plantry-1mu   feature (ok)    class:improvement  needs-spec            theme:intake        net-new LLM weight conversion, vague  ?
   plantry-g0m   task (ok)       class:tech-debt    needs-split           theme:ui-components "investigate and prune..." -- open-ended, several issues

   LABEL CLEANUP
   delete stale label `foo` (0 open issues)
   ```

   Then ask the user to **approve all / edit specific rows / reject**. If they
   edit, adjust and re-show the affected rows before applying.

4. **Apply on approval.** Group the `bd` writes to be efficient — one
   `bd label add class:bug <id1> <id2> ...` per label across all issues that
   share it, likewise for themes and flags; `bd update <id> --type=<t>` for each
   type correction. Confirm what was written.

5. **Report the result.** State the new state in one or two lines: how many
   issues now carry a `class:`, how many leaks (bug+ux) vs parked
   (improvement+tech-debt), how many flagged `needs-spec`/`needs-split` (can't
   just start), and anything you flagged `needs-human`. If you were invoked by
   `triage`, hand back control so it can produce the report on the now-clean
   data.

## Notes

- **Re-runnable and incremental.** Because untriaged is defined by the *absence*
  of a `class:` label, running groom again only touches newly-filed issues — it
  never re-litigates settled ones. That's the point of the convention.
- **One `class:` per issue.** If you ever find two `class:` labels on an issue,
  that's drift — resolve to the single best fit and remove the other.
- **Don't invent themes carelessly.** A sprawl of one-issue themes is as useless
  as no themes. If an issue doesn't fit an existing theme and wouldn't plausibly
  gain siblings, reach for the nearest existing theme before minting a new one.
- This skill only ever changes bd metadata (labels, type). It does not touch
  priority — that's `triage`'s call — and never touches source.
