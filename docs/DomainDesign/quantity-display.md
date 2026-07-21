# Quantity Display — Fractions & Unit Simplification on Scaling

**Status:** Accepted design · **Author:** design conversation (mgasparel + Claude) · **Date:** 2026-07-10
**Amended:** 2026-07-11 — Q5 firewall redesigned from integer-ratio heuristic to an explicit
`UnitSystem` tag, and seeded volume factors changed to nutrition-label values, after the seed-factor
gap (`quantity-display-seed-factor-gap.md`). Work tracked as `plantry-5yde`, blocking `plantry-vci8.3`.
**Scope:** Two presentation-layer behaviours: (Q1) *fraction rendering* — quantities in fraction-styled
units render as vulgar fractions ("½ cup", "1¾ tsp") instead of decimals; (Q2) *unit simplification* —
when a recipe is viewed at a scale ≠ 1, a scaled quantity may be re-expressed in a same-dimension
sibling unit when that reads better ("4 tbsp" → "¼ cup"). Both are formatting concerns: nothing about
storage, scaling math, consumption, shortfall, or shopping changes.
**Out of scope:** fraction *input* parsing (users still type decimals), quantity display on shopping /
pantry / intake surfaces, metric magnitude roll-up (1000 g → 1 kg), cross-dimension conversions.

---

## 1. Motivation

Recipes store quantities as decimals (`Ingredient.Quantity: decimal` + `UnitId`), and every render
site formats with `ToString("0.###")`. That gives "0.5 cup" where any cookbook — and any human —
writes "½ cup". It gets worse under scaling: doubling a recipe with "2 tbsp" produces "4 tbsp",
which a cook reads as "¼ cup" and now has to convert in their head. The goal is that recipes stay
readable *as humans write them* both at rest and as they scale.

The data model already carries everything needed. Units are household-defined within a dimension
with an exact `FactorToBase` (`Unit.cs`), so "16 tbsp = 1 cup" is derivable per household — no
hardcoded imperial table. And because quantities are stored as plain decimals, the entire feature
is a pure formatting layer at the render edge; the blast radius on domain logic is zero.

Precedent: `Details.cshtml.cs::FormatBatchHint` already snaps batch ratios to vulgar-fraction
glyphs for the "½ batch" hint. This design promotes that ad-hoc pattern into a real, shared,
unit-aware formatter and retires the ad-hoc glyph switch in favour of it.

## 2. Decision summary

| # | Decision | Rationale |
|---|----------|-----------|
| Q1 | Both behaviours are **display-only**. Stored quantities, scaling math, consumption, shortfall, and availability comparisons stay decimal in the authored unit; nothing is ever written back in a simplified form | Zero blast radius; simplification bugs can only ever produce an odd string, never a wrong deduction |
| Q2 | `Unit` gains a **`DisplayStyle`** (`Decimal` \| `Fraction`), default `Decimal`. Households edit it on the Catalog → Units page | Units are household-defined, so the code cannot know "cup is imperial". "½ kg" never happens; "½ cup" always does. Explicit beats heuristic-by-code, which breaks on renamed/custom units |
| Q3 | Fraction vocabulary is **halves, thirds, quarters, eighths** — the nine glyphs ½ ⅓ ⅔ ¼ ¾ ⅛ ⅜ ⅝ ⅞ — with mixed numbers ("1½"). Snap tolerance: the fractional remainder must be within **0.01** of a vocabulary fraction; otherwise fall back to the existing `0.###` decimal | Covers every measure in a physical cup/spoon set; the tolerance absorbs stored thirds (0.33, 0.333, 0.67). 0.3 does *not* snap to ⅓ (off by 0.033) — a value the author typed as 0.3 stays 0.3 |
| Q4 | Unit simplification runs **only when scale ≠ 1**. At 1×, the authored unit is always kept (fraction-formatted per Q2/Q3) | The author wrote "4 tbsp" deliberately — maybe that is the measure they own. Scaling is where machine-produced amounts appear; that is where the machine may re-express them |
| Q5 *(amended 2026-07-11)* | `Unit` gains a **`UnitSystem`** tag (`Unspecified` \| `Metric` \| `UsCustomary`). Candidate units for simplification: same **dimension**, same household, same **non-`Unspecified` `UnitSystem`**, both **`DisplayStyle = Fraction`**, and a **whole-number** conversion ratio between authored and candidate unit (or its inverse, exact per `FactorToBase` arithmetic) | The system tag is the metric/imperial firewall — explicit, not an arithmetic coincidence. The original design used the integer ratio *alone* as the firewall, which failed both ways against real seeded data: truncated factors made every real imperial pair fail the 1e-9 test (tbsp/tsp = 3.0000081), while cup = 240 ml made a metric→imperial rewrite *pass* it (480 ml → 2 cup). See `quantity-display-seed-factor-gap.md`. The integer-ratio check is retained only as the math guarantee that a rewrite lands on clean fractions. Restricting to Fraction-styled units keeps simplification inside scoop-measured units where the readability win lives |
| Q6 | Among representations that snap (Q3), pick the lowest **measure-count score**; ties keep the authored unit, then prefer the larger unit. The golden-example table (§5) is the normative contract; the score weights are an implementation detail tunable within it | Encodes "which needs fewer scoops": ¼ cup (one scoop) beats 4 tbsp (four); 2 tsp beats ⅔ tbsp; 3 tbsp stays 3 tbsp because 3∕16 cup doesn't snap at all |
| Q7 | A quantity renders in **exactly one unit**, always. If no candidate snaps, the scaled amount renders in the authored unit — fraction if it snaps, `0.###` decimal if not | Single-unit output is an invariant of the formatter, not a fallback |
| Q8 | Formatter lives in **`Plantry.Catalog.Domain`** as a pure static `QuantityDisplay`, mirroring `UnitConverter`: callers supply the household's units; nothing is loaded | Same-dimension factor math and `Unit` knowledge already live in this context; pure + side-effect-free makes the golden table directly executable as unit tests |
| Q9 | Interactive inputs are untouched: quantity fields (recipe editor, Cook override stepper) keep decimals in the authored unit. Once a Cook line is **overridden**, it displays the override verbatim (decimal, authored unit) — pretty rendering applies only to machine-computed defaults | A user editing "4" (tbsp) while the line reads "¼ cup" is incoherent. An override *is* authored intent, so Q4's logic applies to it |
| Q10 | Seeding: the reference-data seeder marks cup / tbsp / tsp `Fraction` for new households; a one-time data migration does the same for existing households by unit code. Everything else defaults `Decimal` | Sensible out-of-box behaviour; households adjust on the Units page |

## 3. Q1 — Fraction rendering

`QuantityDisplay.FormatAmount(decimal amount, DisplayStyle style) → string`

1. `style = Decimal` → current behaviour: `amount.ToString("0.###", InvariantCulture)`.
2. `style = Fraction`:
   - Split into whole part `w` and remainder `r`.
   - Find the vocabulary fraction (Q3) minimising `|r − p/q|`; snap if within **0.01**.
   - Snapped: render `w` + glyph with no separator ("1½"), glyph alone when `w = 0` ("½"),
     `w` alone when `r` snaps to 0 or 1 (carrying into `w`).
   - Not snapped: decimal fallback for the whole amount (`0.###`).
3. `amount ≤ 0` or null quantity ("to taste" lines): untouched by this feature.

Unit codes render after the amount exactly as today ("½ cup" — code unchanged, no pluralisation
work in this design).

## 4. Q2 — Unit simplification

`QuantityDisplay.Simplify(decimal scaledAmount, Guid unitId, IReadOnlyList<Unit> units) → (decimal amount, Guid unitId)`

Called only when scale ≠ 1 (Q4), before `FormatAmount`. Steps:

1. **Candidates** (Q5): the authored unit itself, plus every household unit with the same
   `Dimension`, the same non-`Unspecified` `UnitSystem` as the authored unit,
   `DisplayStyle = Fraction`, and an integer conversion ratio to/from the authored
   unit via `FactorToBase` division (the existing `SameDimensionFactor` arithmetic, exposed or
   duplicated as needed). An authored unit whose system is `Unspecified` anchors no family:
   the candidate set is itself alone.
2. **Convert** the scaled amount into each candidate; discard representations that do not snap
   under the Q3 rule (whole numbers count as snapped).
3. **Score** each surviving representation `w + p/q` (measure-count heuristic, Q6):
   `score = w + fractionPenalty(p/q) + (1 if unit ≠ authored unit else 0)`
   with `fractionPenalty`: none 0 · ½ 1 · ¼ 2 · ¾ 3 · ⅓ 3 · ⅛ 3 · ⅔ 4 · ⅜ 4 · ⅝ 5 · ⅞ 5.
4. **Pick** the minimum score; ties resolve to the authored unit, then to the larger unit.
   If nothing snaps in any unit, return the input unchanged (Q7 fallback handles rendering).

The weights model "how many measures do I pick up": whole scoops cost 1 each, a partial scoop
costs more the fiddlier the fraction, and changing the author's unit costs 1. They are tunable —
§5 is the contract, not the table above.

## 5. Golden examples (normative)

Assumes the seeded US relationships: 3 tsp = 1 tbsp, 16 tbsp = 1 cup; g/ml/kg are `Decimal`-styled.

| Authored | Scale | Displays as | Why |
|---|---|---|---|
| 0.5 cup | 1× | ½ cup | Q1 snap; no simplification at 1× |
| 0.333 cup | 1× | ⅓ cup | tolerance absorbs the stored decimal |
| 0.3 cup | 1× | 0.3 cup | 0.3 is not ⅓; decimal fallback |
| 4 tbsp | 1× | 4 tbsp | authored unit kept at 1× (Q4) |
| 2 tbsp | 2× | ¼ cup | score 3 (0+2+1) beats 4 tbsp (4) |
| 2 tbsp | 1.5× | 3 tbsp | 3∕16 cup doesn't snap; only survivor |
| 8 tbsp | 2× | 1 cup | score 2 beats 16 tbsp |
| 1 tbsp | 0.5× | ½ tbsp | 1½ tsp scores 3 (1+1+1); ½ tbsp scores 1 |
| 1 tsp | 2× | 2 tsp | ⅔ tbsp scores 5 (0+4+1); 2 tsp scores 2 |
| 2 tsp | 3× | 2 tbsp | score 3 (2+0+1) beats 6 tsp (6) |
| 1 tsp | 3× | 1 tbsp | score 2 beats 3 tsp (3) |
| 1 tsp | 5× | 5 tsp | 1⅔ tbsp scores 6 (1+4+1); 5 tsp scores 5 |
| ¼ cup (0.25) | 3× | ¾ cup | snaps in authored unit; no better sibling |
| 250 ml | 2× | 500 ml | ml is `Decimal`-styled: no fractions, no simplification |
| 100 g | 1.37× | 137 g | decimal units untouched |
| 0.5 cup | 1.1× | 0.55 cup | 0.55 snaps to nothing; decimal fallback in authored unit |

These are the acceptance tests, verbatim, plus tolerance-boundary cases (0.32 / 0.34 around ⅓)
and the firewall exclusions (Q5): authored tbsp with ml present in the household → ml never
proposed *even though 15 ml : 1 ml is a whole-number ratio* (different `UnitSystem`), and the
reverse — authored ml with a Fraction-styled cup present → cup never proposed (480 ml stays
480 ml, the regression that motivated the amendment).

## 6. Domain & schema changes

- `Unit` gains `DisplayStyle DisplayStyle` (enum `Decimal = 0`, `Fraction = 1`) with a
  `SetDisplayStyle` mutator; EF migration adds the column, default `Decimal` (Q2).
- One-time data migration: units with code in (`cup`, `tbsp`, `tsp`) case-insensitively →
  `Fraction` (Q10). `CatalogReferenceDataSeeder` sets the same for new households.
- New pure static `Plantry.Catalog.Domain.QuantityDisplay` (Q8) with the two functions in §3–§4.
- No changes to `Ingredient`, `Quantity` storage, `UnitConverter`, or any command path.

Added by the 2026-07-11 amendment (Q5, implemented as `plantry-5yde`):

- `Unit` gains `UnitSystem UnitSystem` (enum `Unspecified = 0`, `Metric = 1`, `UsCustomary = 2`)
  with a `SetUnitSystem` mutator, mirroring the `DisplayStyle` pattern; EF migration adds the
  column, default `Unspecified`. Catalog → Units page gains a system selector alongside the
  display-style control. Count units stay `Unspecified` — count-dimension simplification
  (12 ea → 1 doz) is deliberately out of scope; add a `Count` system value if ever wanted.
- Backfill by unit code (case-insensitive): `ml`, `l`, `g`, `kg`, `mg` → `Metric`;
  `oz`, `lb`, `fl oz`, `cup`, `tsp`, `tbsp` → `UsCustomary`. User-created units stay
  `Unspecified` (never cross-propose) until the household classifies them.
- Seeded volume factors change to the **US nutrition-label values**: tsp = 5, tbsp = 15,
  fl oz = 30 (cup is already 240). Within-family ratios become exactly 3 / 2 / 8 / 16 — the
  integer-ratio math guarantee now actually passes for real households. The data migration
  updates existing households' rows **only where the factor still equals the old seeded value**
  (4.92892 / 14.7868 / 29.5735), preserving any hand-edited factor. Rationale for label values
  over full-precision customary (236.5882365 etc.): the 1.4% physical difference is immaterial
  in both costing (price noise ≫ 1.4%, computed fresh, nothing stored) and future nutrition
  (US label tolerance is ±20%), so with the system tag carrying the firewall, factor choice is
  cosmetic — round numbers win. All factor literals must stay in `decimal`/`numeric` end to end
  (they already do: `FactorToBase` is `decimal`, column is unconstrained `numeric`); migration
  SQL uses decimal literals, never float casts or computed expressions.

## 7. Integration surfaces (v1)

| Surface | Behaviour |
|---|---|
| Recipe **Details** ingredient list | `FormatAmount` at 1× (Details renders default servings) |
| **Cook** page line quantities | `Simplify` (scale ≠ 1) → `FormatAmount` for the machine-computed default; overridden lines show the override verbatim (Q9). Availability/shortfall text is unchanged and stays in the authored unit — comparisons already run on decimals |
| Recipe **editor**, Cook **override inputs** | untouched — decimal inputs in the authored unit (Q9) |
| Catalog → **Units** page | new display-style control per unit (seg-ctrl per the component library) |
| Batch hint (`FormatBatchHint`) | re-implemented on `FormatAmount` so glyph logic lives once |

Shopping, pantry, take-stock, and intake keep `0.###` in v1 — those surfaces display *stock*
(weighed/counted), not *measures*, and the readability win is in recipes.

## 8. Testing

- `QuantityDisplay` unit tests: the §5 golden table verbatim, tolerance boundaries, tie-break
  cases (authored-unit preference), the firewall exclusions (both directions, per §5), `Decimal`-style
  passthrough, and null/zero handling.
- An L3 test asserting `Simplify` re-expresses across the **real seeded units** (the seeder's
  actual output, not synthetic exact-multiple test units) — the class of gap the original
  golden tests could not catch, since they deliberately used synthetic factors.
- `Unit.SetDisplayStyle` / `Unit.SetUnitSystem` + migration defaults covered in existing Catalog
  domain/infrastructure test patterns.
- One rendering assertion each on Details and Cook (existing page-model test style) proving the
  formatter is wired — the formatting logic itself is exercised at the domain layer, not through
  the UI.
