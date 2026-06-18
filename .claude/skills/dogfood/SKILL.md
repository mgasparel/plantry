---
name: dogfood
description: >-
  File a beads issue from direct use of the Plantry app. USE FOR: "I just
  noticed X in the app", "when I tried to do Y, Z happened", "the app feels
  off when I...", logging observations from real usage sessions. DO NOT USE
  FOR: planned feature ideas disconnected from actual use (use `bd create`
  directly), or code-review findings (use the plantry-code-review skill).
license: MIT
metadata:
  author: plantry
  version: "0.1.0"
---

# Dogfood

Filing observations from actually using the app is the highest-fidelity
feedback loop. This skill turns a raw observation into a well-labelled beads
issue in one shot — so the friction of "I'll file it later" never eats the
insight.

The deliverable is a single `bd create` call that captures what the user saw
or felt, which part of the app it happened in, and whether it's a bug (wrong),
UX friction (rough), or a missing capability.

## Procedure

1. **Read the input.** The observation may arrive as skill args (the text after
   `/dogfood`) or as a message in the active conversation — accept whichever is
   present. If there is nothing to work with, ask in one sentence: "What did
   you observe while using the app?"

2. **Classify the observation** — pick exactly one:
   - `class:bug` — the app did something incorrect: wrong value, missing data,
     crash, broken action, unexpected navigation.
   - `class:ux` — the app worked but the experience is rough, confusing, slow,
     or a noticeable gap (e.g. empty states, unclear feedback, awkward flow).
   - `class:improvement` — a genuinely new capability the user noticed is
     missing; the absence isn't a defect.

   If the description is unambiguous, classify silently. If it straddles two
   classes, pick the dominant signal and note the secondary one in the body.

3. **Identify the theme** — map to the nearest existing `theme:` label:

   | Theme | Covers |
   |---|---|
   | `theme:home` | Today / Home page |
   | `theme:intake` | Receipt / grocery intake flow |
   | `theme:inventory` | Pantry / stock management screens |
   | `theme:meal-planning` | Meal plan pages, plan rail, meal editor |
   | `theme:recipes` | Recipe list, edit, and detail pages |
   | `theme:shopping` | Shopping list screens |
   | `theme:tags` | Tag management |
   | `theme:ui-components` | Design-system or component-level issue spanning pages |

   If the screen is clear from the description, pick silently. If ambiguous,
   ask one question: "Which part of the app — home, recipes, meal planning,
   shopping, inventory, intake, or something else?"

4. **Check for `quick-win`.** Apply it if the fix looks small and contained
   (a single wrong label, a missing null check, one CSS tweak). Leave it off
   when unsure.

5. **Draft the issue** and show it before filing:

   ```
   PROPOSED DOGFOOD ISSUE

   title:    {short imperative title — e.g. "Stock count not updated after consume"}
   type:     {bug | feature | task}
   class:    {class:bug | class:ux | class:improvement}
   theme:    {theme:X}
   labels:   source:dogfood{, quick-win if applicable}

   description:
     Observed while using the app: {one paragraph in plain language, quoting
     what the user said. Include screen/flow context. Avoid inventing details
     not in the input.}

   [file it / edit / cancel]
   ```

   Type mappings: `class:bug` → `type:bug`; `class:improvement` → `type:feature`;
   `class:ux` → `type:task`.

6. **File on approval.** Run:

   ```bash
   bd create "{title}" \
     --type {type} \
     --description "{description}" \
     --labels "class:{x},theme:{x},source:dogfood{,quick-win}"
   ```

   Report the new issue ID. If the user edits the draft, update and re-show
   before filing.

## Notes

- **One observation per issue.** If the user describes two distinct problems,
  propose two issues. Compound issues hide one of the two problems from triage.
- **Quote the user, don't paraphrase.** The description should capture what was
  actually said — future triagers shouldn't have to guess what the user meant.
- **Don't over-spec.** Dogfooding issues are raw observations. Leave the "how
  to fix it" for the implementer.
- **`source:dogfood` is a provenance label** — like `code-review`, it records
  where the issue came from, not how urgent it is. `triage` and `groom` leave
  it alone.
- This skill only ever creates issues — it never modifies existing ones.
