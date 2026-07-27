# Brand placement & pack-unit conversion ambiguity

*Design discussion notes, 2026-07-25. Status: analysis only — no decisions committed, no code changed.*

Two related modeling questions, one shared invariant:

1. At what level does "Brand" live — are two brands of oat milk variants or SKUs?
2. Pack-shaped units ("can") in recipes are ambiguous when a product has multiple
   pack sizes of the same shape.

---

## 1. Where Brand lives

### What the code says today

- **Stock is held per product, per household.** `ProductStock` is keyed
  `(household_id, product_id)` (`src/Plantry.Inventory/Domain/ProductStock.cs`).
- **Only concrete products hold stock.** `Product.CanHoldStock => !HasVariants`
  (`src/Plantry.Catalog/Domain/Product.cs:78`) — a parent product is an abstract
  grouping; only leaves (standalone products or variants) hold stock. Depth is
  capped at 1.
- **SKU is a pack-size descriptor, nothing more.** `ProductSku` = label + size
  quantity + size unit ("2 L carton", "500 g bag"). Stock lots reference SKU only
  as optional provenance (`StockEntry.SkuId`); no quantity accounting happens at
  SKU level. Per `docs/DomainDesign/DataModels/catalog.md`: *"stock aggregates at
  product level; price/intake reference SKU."*
- **Catalog has no Brand field.** Brand exists as a string only in the Deals
  context (`Deal`/`RawDeal`), sourced from flyer parsing.

### Decision rule

Brand is **not a taxonomy level**. It is one of several reasons a user might decide
two things are not interchangeable. The parent/variant mechanism captures that
decision directly:

- **User cares which brand is on hand** (taste, price, expiry behaviour differ):
  each brand becomes a **variant** — parent "Oat milk" with children "Oatly" and
  "Earth's Own". Each variant is a concrete product with its own `ProductStock`,
  expiry defaults, and low-stock threshold. The parent becomes the generic handle
  and stops holding stock.
- **User doesn't care** (any oat milk is oat milk): one product, stock pooled;
  brand is at most noise in the product name or SKU label.

**Brand is never a SKU.** A SKU has no inventory identity — two brands modeled as
two SKUs could never be counted separately.

**Recommendation:** do not add a Brand column to Catalog. A forced
Brand → Variant → SKU hierarchy doesn't survive grocery reality (is "PC 2%
lactose-free" a brand variant or a kind of milk?). Let the user's
interchangeability judgment drive the split, lazily — don't split until they care.

**Cost of splitting:** once a product splits into variants, anything pointing at
the parent (recipes, shopping-list matching, deal matching) needs
"parent aggregates its variants' stock" behaviour. This is why defaulting to *not*
splitting is the right lazy path. (Cook-time side of this is already designed:
DM-19/C11 Variant Disambiguation Picker.)

---

## 2. Pack-unit conversion ambiguity ("1 can" problem)

### Problem statement

Recipe says "1 can". The product has three can-shaped SKUs at different sizes
(156 ml / 400 ml / 796 ml). Which factor converts "can" to a measurable quantity?

### What the code says today

- **Conversions live on Product, not SKU.** `ProductConversion` is a child of
  `Product` (DM-12); `UnitConverter.Convert` resolves purely from product-level
  conversions and unit dimensions — it never consults SKU sizes.
- A SKU's size (`size_quantity` + `size_unit`) is an *implicit* per-pack
  conversion ("this pack = 400 g") that the conversion machinery ignores.
- **Sharp edge:** `Product.AddConversion` deliberately does **not** dedupe two
  user-confirmed conversions for the same `(fromUnit, toUnit)` pair, and
  `UnitConverter.Convert` takes the **first** matching conversion in list order.
  Three "can" factors are representable today and would resolve silently and
  arbitrarily — no error, just a wrong deduction.

### Analysis

"Can" is not a unit of measure; it is a **pack descriptor**. Grams/ml are
properties of the substance; "can" is a property of the packaging, so its true
factor is per-SKU by definition. A product-level `can → ml` conversion is coherent
only when "a can of this product" means one thing.

**Shared invariant (both questions reduce to this):**

> Within one product, a unit token must mean exactly one thing.

### Options considered

| Option | Verdict |
|---|---|
| **A. Per-SKU conversions** (move "can" factors down to SKU) | Rejected. Conversion then needs a SKU context, which exists at consume time (`StockEntry.SkuId`) but not at recipe authoring or fulfillment time — the ambiguity moves to planning, where there is no SKU to ask. Also contradicts R7 (recipes: "a tracked unit must have a conversion path to the product's unit"), which is built on product-level resolution. |
| **B. Resolve at authoring time** | Recommended. "1 can" in a recipe is authoring intent — the author meant a *specific* can; the recipe is calibrated to ~400 ml regardless of which can the pantry later holds. If the product has multiple can-shaped SKUs (or multiple candidate factors), disambiguate at write time, when the user is present to answer — not at consume time, when the system must guess. |
| **C. Split the product** | Complementary escape valve. If a household genuinely treats 156 ml and 796 ml cans as different things (tomato paste vs diced tomatoes, usually), the product boundary is wrong — separate products/variants, each with one unambiguous "can" conversion. Same signal as the brand question. |

Open sub-choice within B: store "1 can" + product conversion (recipe silently
shifts if the user later edits the factor), or store the resolved measure with
"1 can (400 ml)" as display text (recipe stable). Leaning toward storing the
resolved measure; either way the ambiguity must die at write time.

### Recommendations

1. **Keep conversions at product level.** Do not add per-SKU conversions.
2. **Enforce the invariant instead of hoping:** dedupe user-confirmed
   `ProductConversion` pairs — the current non-dedupe is a latent trap independent
   of this discussion.
3. **Authoring-time disambiguation** when a recipe author picks a pack unit that
   is ambiguous for the product (multiple pack-shaped SKUs / candidate factors).
4. **Escape valve** for genuinely different pack semantics is a product split
   (variants), not a fourth modeling level.

### Open question (determines urgency)

What unit does **intake write stock lots in**? Lots carry `SkuId` plus a quantity
in an arbitrary unit:

- If intake books "2 cans" as `quantity=2, unit=can`, the ambiguity infects stock
  accounting itself.
- If it books the measured quantity (800 g, SKU recorded as provenance), stock
  stays clean and this is purely a recipe-authoring UX concern.

Not yet verified against the intake commit path.

---

## Addendum (plantry-xddq, 2026-07-25): `UnitConverter.Convert` now walks a graph

A related but distinct bug shipped after this note was written: `Convert` only ever
resolved a *single* `ProductConversion` hop, and its same-dimension fast path matched
any two units sharing a `Dimension` — including two unrelated `Dimension.Count` units
on the same product (e.g. "srv" and "pk") — silently returning a bogus 1:1-ish ratio
instead of failing loudly or chaining the product's configured conversions through a
shared pivot (e.g. `srv → cup → g → pk`).

Fixed in `src/Plantry.Catalog/Domain/UnitConverter.cs`: `Convert` now treats units as
graph nodes and BFS-walks from `fromUnitId` to `toUnitId`, composing same-dimension
scale edges (Mass/Volume only) and `ProductConversion` edges (both directions)
transitively. Two distinct `Dimension.Count` units connect **only** via an explicit
`ProductConversion` — never for free — closing the collision above.

This does **not** change anything in this doc's analysis: conversions are still
product-level only (no SKU involvement), and the "first matching conversion wins,
duplicates aren't deduped" sharp edge above is still exactly as described — the graph
walk preserves that same resolution-order preference (same-dimension edges before
conversion edges; conversions walked in list order) at every hop, it just no longer
stops after one hop.

One practical side effect worth flagging: the reference-data seeder's `doz` unit
(`Dimension.Count`, `factor_to_base = 12`) encoded "1 dozen = 12 each" as if it were a
universal physical ratio, the same way `kg`/`g` are. Under the new rule that only
Mass/Volume get a free same-dimension hop, `doz ↔ ea` no longer resolves for a product
unless that product also has an explicit `ProductConversion` between them — the fix
treats `doz`/`ea` the same as `srv`/`pk` (no ProductConversion, no free ratio), which is
correct per the ticket's rule but means seeded `doz` is inert until a product configures
it. No code currently relies on the old free `doz↔ea` hop (verified — no test or
production caller round-trips them without a `ProductConversion`).

**Resolved (plantry-qszb, 2026-07-27):** `pk`/`doz` are no longer seeded by
`CatalogReferenceDataSeeder` — only `ea` and `srv` are. Existing households were
relabeled from `pk`/`doz` to `ea` by a one-time data migration chain
(`20260727061526_RemovePackAndDozenUnits.cs` in Catalog, a
`RelabelPackAndDozenUnitReferences` migration in every other bounded context holding a
soft `unit_id` reference, and a final delete in Housekeeping). A household that wants
dozen-style tracking creates a custom count unit (Catalog → Units) and adds a
`ProductConversion` for it, the same as any other product-specific count ratio.
