---
name: plantry-code-review
description: >-
  Review Plantry code changes for both standard correctness/quality issues AND
  this project's specific architectural conventions and product values — DDD
  bounded-context discipline, household tenancy, the single consumption
  primitive, AI-as-untrusted-input staging, hypermedia-only UI (no SPA/Node),
  and persistence conventions. USE FOR: reviewing a diff, PR, or recently
  written/edited code in src or tests; "review this", "code review", "does this
  follow our conventions", pre-commit/pre-PR self-checks.
  DO NOT USE FOR: generic reviews of code outside this repo.
license: MIT
metadata:
  author: plantry
  version: "0.2.0"
---

# Plantry code review

Plantry is a DDD modular monolith with a deliberately narrow architecture: nine
bounded contexts in one process/database, hypermedia UI (no SPA, no Node), AI treated
as untrusted input, and one primitive for every stock removal. Code that's clean but
violates one of these decisions is a regression even if it compiles and passes tests —
that's the kind of drift this review exists to catch. Run the standard pass first, then
the Plantry-specific gates.

## Standard review pass

- Correctness: logic errors, edge cases, off-by-ones, null/empty handling, async/await
  misuse, race conditions, transaction boundaries.
- Security: injection (incl. raw SQL/EF interpolated strings), XSS in Razor output,
  authz checks on every handler, secrets in code/config.
- Tests: do behavior changes have corresponding coverage? Are obvious mutation gaps
  (per `stryker-config.json`) plausible for the change?
- Reuse/simplification: duplicated logic, unnecessary abstraction, dead code.

## Gate 1 — Bounded-context and aggregate discipline

- **No cross-context table reads.** A context's repository/EF queries touch only its
  own schema (`identity`, `catalog`, `inventory`, `intake`, `pricing`, `recipes`,
  `meal_planning`, `shopping`, `deals`). If `Plantry.Recipes` needs Inventory data, it
  calls Inventory's application service or reads its read model — it never queries
  `inventory.*` tables directly.
- **Cross-context references are IDs only, never embedded entities.** A
  `Recipe.Ingredient` holds a `ProductId`, not a `Product`. Catalog is the universal
  upstream supplier — nothing downstream mutates a `Product`/`Unit`/`Location`/
  `Category`.
- **One aggregate per transaction.** A single `SaveChanges`/transaction mutates one
  aggregate root. Multi-aggregate fan-out (cook issuing N `Consume` calls, Intake's
  commit orchestration) is the *only* sanctioned exception, and even there each
  downstream call is its own transaction, driven by an application service — never one
  cross-aggregate save.
- **Invariants stay inside the aggregate.** Mutation goes through guarded methods on
  the root — private constructor + static `Create` factory + methods like
  `UpdateName`/`SetExpiryWarningDays` that validate before assigning (this is the
  existing house style, e.g. `Household`). No public setters, no reaching into a
  child collection from outside the aggregate.

## Gate 2 — Household tenancy

- Every aggregate root carries `household_id`, and every query filters by it. A
  repository method that can return rows without `WHERE household_id = @id` — relying
  on Postgres RLS alone, or vice versa — is a tenant-isolation bug. **Treat this as
  blocking**, not a style note: both the repository filter and RLS (`SET
  app.household_id`) must be present as defense-in-depth.
- New tables that hold household-scoped data need RLS policies wired up consistent
  with existing migrations.
- `user_id` rides along on journal/audit rows for attribution.

## Gate 3 — The single consumption primitive

- **Every stock removal flows through `ProductStock.Consume(quantity, unit, reason,
  sourceRef?)`.** Any new code that decrements `StockEntry.quantity` directly, or
  writes a removal-shaped journal row outside `Consume`, is a violation — flag it even
  if it "only" handles one new case. A new removal trigger adds a `reason`/
  `source_type` value to the existing taxonomy; it does not open a parallel write path.
- **`reason` (Consumed / Discarded / Correction) and `source_type` (Intake / Manual /
  Cook / …) are separate axes** — don't conflate "why stock left" with "what triggered
  it." Watch for code that still uses the old shorthand (e.g. `reason: Cook`) instead
  of `source_type: Cook`.
- Inventory stays ignorant of recipes and substitutions — that vocabulary belongs to
  Recipes' cook orchestration, which issues plain `Consume` calls per resolved
  ingredient.
- Quantity-neutral lot transitions (transfer, freeze/thaw, open) update `StockEntry`
  state directly and write **no** journal row — don't "fix" that by routing them
  through `Consume` or by adding journal entries for them.

## Gate 4 — AI as untrusted external function

- Model calls happen **server-side only**, through the `ChatClient` abstraction. No
  client-side calls, no API key reachable from the browser.
- AI output lands in a **staging aggregate** (`ImportSession`/`ImportLine`,
  `MealPlanProposal`) as a proposal — raw model output kept in `raw_parse` jsonb for
  provenance — and *only* an explicit user confirmation may trigger writes into
  Inventory/Catalog/Pricing/MealPlan. Any path that lets AI output write straight into
  a core aggregate without that review step is a violation, no matter how high the
  model's confidence score.
- Only user-*resolved*, typed fields commit. Don't let convenience code promote raw AI
  fields straight into typed columns.

## Gate 5 — Hypermedia-first UI: no SPA, no Node

- No front-end framework, bundler, `package.json`, or `node_modules`. This was a
  deliberate, weighed rejection (dependency surface, payload size, second build
  pipeline) — flag any new JS dependency that isn't htmx or Alpine, and any build step
  that isn't `dotnet`/`dotnet watch`.
- Server-driven interaction is **htmx fragment swaps**; local/draft client state is
  **Alpine `x-data`**. Hand-rolled `fetch` + DOM manipulation that duplicates what
  either library already does is a smell.
- The server renders domain state as HTML directly — there is no client-side shadow
  model of domain data. JS that recomputes something the server already computed
  (fulfillment %, cost per serving, totals) is exactly the drift this rules out.
- The recurring composite widget — catalog search + cascading unit load + quantity —
  should be a shared server partial / units endpoint, not copy-pasted per screen (it
  appears in manual add, import review, recipe authoring, cook substitution, shopping).
- **Component library is the single source of truth.** `src/Plantry.Web/Pages/Dev/Index.cshtml`
  is the definitive catalogue of every reusable Razor tag helper/partial and canonical
  CSS pattern (cards, segmented controls, buttons, field rows, etc.). Flag any markup
  that re-implements a pattern already in the library instead of using the canonical
  form, and flag any new component added directly to a feature page that bypasses the
  library. New components must be added to the library first, then consumed by feature
  pages — if a PR adds both without that order, call it out as advisory.

## Gate 6 — Persistence conventions

- PKs are app-generated `uuid` (UUIDv7) — not identity columns, not DB-generated UUIDs.
- Every aggregate root has `household_id`; children reference their parent via a
  composite FK `(household_id, parent_id)` against the parent's
  `UNIQUE (household_id, id)`.
- One Postgres schema per bounded context; cross-context references are bare IDs with
  **no enforced FK** (hard FKs only within a context/aggregate).
- `created_at`/`updated_at` are `timestamptz` (UTC). Money is `numeric(12,2)`,
  quantity `numeric(12,3)`, conversion factors `numeric(18,6)`.
- Catalog reference data is **soft-deleted** (`archived_at`); journal and
  price-observation tables are **append-only** — corrections are new rows, never
  updates or deletes.
- Enums are C# enums persisted as `text` + `CHECK` constraint, not Postgres `ENUM`.
- Receipt images and recipe photos live in PostgreSQL (`bytea`/large objects), not on
  disk or in object storage.

## Gate 7 — Does this serve the product, or fight it?

A judgment call, not a mechanical check — raise it as an observation, not a blocker,
unless it's egregious:

- Plantry's bet is that **friction is the problem**. Does this change make a common
  flow (logging groceries, checking what's cookable, reviewing an import) slower, more
  manual, or more demanding of user discipline than before?
- Recipes, pantry, and cost are supposed to stay connected — features that make the
  user cross-reference them by hand are working against the product's core thesis.
- Watch for drift toward what Plantry deliberately isn't: a meal-kit/subscription
  model, social/sharing features, or a dependency on barcode scanning / external
  product databases.

## Output format

Group findings by gate. For each: **file:line**, what's wrong and why it matters in
*this* codebase (name the rule, e.g. "bypasses the single consumption primitive" or
"leaks across the bounded-context boundary" — not just "this looks off"), and a
concrete fix, ideally pointing at an existing pattern to mirror.

Severity: tenancy leaks (Gate 2) and consumption-primitive bypasses (Gate 3) are
correctness/security bugs — call them out as blocking. UI-library and vision-alignment
notes (Gates 5, 7) are advisory — say so, so the user can weigh them on their own terms.

Finally, a judgement must be made whether this is a pass or fail. Does it meet our high
quality standards or not?

The report must be written to a file on disk at ./.reviews/{timestamp}-{branch}.md