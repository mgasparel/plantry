# Seed-factor gap: cup is not an exact multiple of tbsp/tsp

**Status:** RESOLVED 2026-07-11 — decision recorded in §Resolution below; spec amended
(`quantity-display.md` Q5); implementation tracked as `plantry-5yde`, a blocker of `plantry-vci8.3`.
**Blocks:** `plantry-vci8.3` (Wire QuantityDisplay into Recipe Details + Cook) resumes once `plantry-5yde` ships.
**Related bead:** `plantry-5yde` (P1) — rewritten from "reconcile seeded volume factors" into the
`UnitSystem` firewall redesign.

## The problem

`CatalogReferenceDataSeeder.cs:38-40` seeds volume unit conversion factors:

```
cup  = 240
tbsp = 14.7868
tsp  = 4.92892
```

Ratios: `240 / 14.7868 = 16.23` and `14.7868 / 4.92892 = 3.0000081`. The tsp→tbsp ratio is
close enough to 3 to be a rounding artifact, but the cup→tbsp ratio is genuinely not a whole
number.

`QuantityDisplay.Simplify` (built in `plantry-vci8.2`, `src/Plantry.Catalog/Domain/QuantityDisplay.cs`)
only proposes re-expressing a quantity in a sibling unit when the two units' `FactorToBase` form
an exact integer ratio (1e-9 tolerance) — this is the deliberate "metric/imperial firewall" from
the `quantity-display.md` spec (Q5), which stops nonsensical proposals like tbsp→ml. Because
cup/tbsp fails that test with the *real* seeded values, `Simplify` will never actually propose
cup for a tbsp-denominated quantity (or vice versa) against a real household — even though the
formatter itself is correct and its golden-table unit tests pass (they deliberately use
exact-multiple synthetic units to test the algorithm in isolation).

**Concretely: the epic's headline example, "4 tbsp → ¼ cup," would render as a silent no-op in
production.** Q1 (vulgar fractions, e.g. "1/2 cup") still works fine — only Q2 (cross-unit
simplification) is affected.

## Why the mismatch exists

Both numbers are individually "correct," they're just two different real-world conventions that
don't compose:

- `cup = 240ml` is the **US nutrition-label legal cup** — a rounded value used for
  Nutrition Facts panel math in US regulation.
- `tbsp = 14.7868ml` / `tsp = 4.92892ml` are the **precise US customary culinary units**
  (1 tbsp = 3 tsp exactly; 1 cup = 16 tbsp exactly in the culinary system = 236.58816ml).

Plantry's seed data mixes the two conventions. This was almost certainly an oversight — nobody
sat down and chose "nutrition-label cup" on purpose, `240` is just the number most people type
from memory — rather than a deliberate design decision.

## Blast radius of fixing it (reconcile cup → 236.58816ml)

Investigated directly in the codebase (not speculative):

**Costing — real dollar impact, but computed fresh, not stored.**
- `src/Plantry.Composition/Pricing/UnitPriceCalculatorAdapter.cs:23` —
  `price / (quantity * unit.FactorToBase)` — normalizes a price observation to $/base-unit.
- `src/Plantry.Recipes/Domain/CostingService.cs` (`CostLineAsync` / `Compute`, ~lines 128-307) —
  recipe cost-per-serving, via `IUnitConverter.ConvertAsync` and
  `UnitConverter.SameDimensionFactor` (`src/Plantry.Catalog/Domain/UnitConverter.cs:114-126`),
  which is `fromUnit.FactorToBase / toUnit.FactorToBase` for any same-dimension pair.
- Any household pricing a product "per cup" while a recipe calls for tbsp/tsp/floz of it (or
  vice versa) will see displayed unit price / cost-per-serving shift by ~1.4% — the same amount
  the cup factor changes.
- **Neither value is persisted** — both are documented as "computed fresh" — so this is a
  one-time, going-forward drift in a displayed number, not corruption of historical stored data.

**Nutrition — not built, not documented, but structurally the same risk.**
- Searched `docs/VISION.md`, `docs/SPEC.md`, `docs/ARCHITECTURE.md`, `docs/FUTURE.md`, all
  `docs/ADRs/*`: zero mentions of nutrition/calorie/macro tracking as a feature, present or
  planned. The only related hit is `docs/DomainDesign/grocy-import-plan.md`, which explicitly
  *drops* Grocy's calorie field as out of scope during migration.
- Owner's mental model for the eventual feature (this conversation, 2026-07-11): **products
  carry nutritional info; recipes roll it up** — i.e., nutrition entered against a product in
  some basis unit, then aggregated across a recipe's ingredient lines.
- **If built that way, it would need the same unit-conversion step `CostingService` already
  does** (product's nutrition basis unit → recipe line's authored unit, via `UnitConverter`).
  That means the cup/tbsp/tsp mismatch is a **latent defect in the volume-unit dimension
  itself**, not a one-off bug local to Q2 display — it will resurface in the same shape
  wherever anything converts across cup/tbsp/tsp: today in costing (silently, already live) and
  Q2 simplification (blocked by this), tomorrow in nutrition rollup (if/when built the same way).

## Options considered

1. **Reconcile now** — data migration setting `cup.FactorToBase = 236.58816` (exact
   `16 * tbsp`), add `plantry-5yde` as a blocker of `vci8.3`, add an L3 test asserting
   `Simplify` actually re-expresses against the *real* seeded units (not just synthetic test
   units). Fixes costing's silent ~1.4% drift, unblocks Q2 for real households, and forecloses
   the same bug recurring in a future nutrition rollup. Cost: touches conversion data outside
   `vci8`'s originally stated "display-only" blast radius (though no stored data needs
   migrating — costing recomputes live).
2. **Ship `vci8.3` as originally scoped, defer `plantry-5yde`** — Q1 (fractions) works, Q2
   (cross-unit simplification) silently never fires for real households until the follow-up
   ships separately. Costing's existing ~1.4% mismatch (already live today, pre-dating this
   epic) is untouched either way.
3. Some other reconciliation strategy (e.g., only fix going forward for new households, leave
   existing households' factor as-is and accept Q2 never fires for them; or pick a different
   canonical cup value entirely).

Leaning toward option 1, but it's the owner's call given the retroactive-data-change angle
inherent in option 1's migration.

## Resolution (2026-07-11, owner decision)

**Decision: fix the class of bug, not the instance — none of the three options above as written.**
Investigation during the decision conversation widened the gap in two ways that reframed it:

1. **Every imperial volume pair fails the firewall, not just cup.** `IntegerRatioTolerance` is an
   absolute 1e-9 on the ratio; the truncated seeds give tbsp/tsp = 3.0000081 and
   fl oz/tbsp = 1.9999932 — both fail. Q2 was dead across the whole family.
2. **cup = 240 breaches the firewall in the reverse direction.** cup/ml = exactly 240, a whole
   number — so an authored "480 ml" at scale ≠ 1 with cup opted into Fraction style would be
   rewritten "2 cup", precisely the cross-system proposal Q5 exists to prevent. Cross-system
   conversions were only absent because the data *happened* not to trigger them.

The root cause is that the integer-ratio check was doing two jobs at once: deciding unit-family
membership (the firewall) and guaranteeing rewrites land on clean fractions (the math). One
arithmetic test can't serve both. The fix decouples them:

- **`Unit` gains an explicit `UnitSystem` tag** (`Unspecified` | `Metric` | `UsCustomary`).
  The firewall becomes *same dimension + same non-`Unspecified` system* — semantic, per-household,
  unbreakable by anyone's choice of factor. The integer-ratio check is kept only as the math
  guarantee within a family.
- **Volume factors re-seed to the US nutrition-label values**: tsp = 5, tbsp = 15, fl oz = 30,
  cup stays 240. With the tag carrying the firewall, factor choice is cosmetic, and the accuracy
  question is a tie — the 1.4% difference vs. full-precision customary values is far below both
  grocery-price noise (costing) and the ±20% US label tolerance (future nutrition) — so round,
  label-aligned, human-readable numbers win. Full-precision customary values
  (4.92892159375 etc.) were considered and rejected *as a pair with the old firewall*: they fix
  the instance but leave the firewall an arithmetic coincidence that the next round-numbered
  unit (a 250 ml metric cup, a custom unit) silently re-breaks.
- Migration updates existing households' factors **only where still equal to the old seeded
  values**, and backfills `UnitSystem` by unit code; user-created units stay `Unspecified` and
  never cross-propose until classified. Mass and count factors are untouched (oz/lb already
  compose exactly: 453.592 / 28.3495 = 16).

Full design in `quantity-display.md` (amended Q5, §6) and the `plantry-5yde` brief.

## Relevant files

- `src/Plantry.Catalog.Infrastructure/CatalogReferenceDataSeeder.cs:38-40` — the seed values
- `src/Plantry.Catalog/Domain/QuantityDisplay.cs` — `Simplify`'s integer-ratio firewall (Q5)
- `src/Plantry.Catalog/Domain/UnitConverter.cs:114-126` — `SameDimensionFactor`
- `src/Plantry.Composition/Pricing/UnitPriceCalculatorAdapter.cs:23` — price normalization
- `src/Plantry.Recipes/Domain/CostingService.cs` — recipe cost-per-serving
- `docs/DomainDesign/quantity-display.md` — the epic's spec (Q2, Q5, Q7)
- `docs/DomainDesign/grocy-import-plan.md:22,76,162` — calorie field explicitly dropped
- Beads: `plantry-vci8` (epic), `plantry-vci8.3` (paused child), `plantry-5yde` (the fix, P1)
