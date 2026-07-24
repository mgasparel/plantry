using Plantry.Recipes.Application;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Domain service that computes the cost-per-serving for a recipe at a given serving count
/// (recipes-domain-model.md §7). Stateless; reads price history via <see cref="IPriceReader"/>,
/// resolves the parent/variant tree via <see cref="ICatalogProductReader"/> (DM-19), and converts
/// units via <see cref="IUnitConverter"/>. Never persisted — computed fresh at query time (J3,
/// recipes-domain-model.md §1).
///
/// Costable set: tracked, real (non-untracked-staple) ingredients only. Untracked staples
/// (<c>track_stock = false</c>, null Quantity/UnitId) are excluded from <c>CostableCount</c>
/// because "no price by design" is not a data gap (C12).
///
/// Parent → variant price rollup (DM-19, mirrors <see cref="FulfillmentService"/>'s stock rollup):
/// price observations only ever land against the concrete product actually purchased (the variant) —
/// the abstract parent (<c>Product.CanHoldStock == false</c>) never receives one. For a
/// parent-referencing ingredient, the line prices from the <b>cheapest converted line cost</b> among
/// its live variants: run the price → unit-price → unit-conversion pipeline once per variant
/// (conversion keyed on the variant's own product id) and take the minimum of the successful
/// candidates. A leaf product simply prices against itself (single-element ref list). A parent with
/// zero live variants, or none of whose variants yield a usable/convertible price, is un-priced —
/// and because <c>MissingPriceProductIds</c> is resolved against ingredient product ids by the UI, the
/// <b>parent's own</b> product id (never a variant id) is what gets added to that list.
///
/// Completeness rules (recipes-domain-model.md §6 / CostCompleteness):
/// <list type="bullet">
///   <item><see cref="CostCompleteness.Full"/> — every costable ingredient has a price
///     (<c>PricedCount == CostableCount &gt; 0</c>); <c>Amount</c> is exact.</item>
///   <item><see cref="CostCompleteness.Partial"/> — some priced, some not
///     (<c>0 &lt; PricedCount &lt; CostableCount</c>); <c>Amount</c> is a flagged under-estimate
///     and <c>MissingPriceProductIds</c> lists the un-priced products. A parent line with only SOME
///     of its variants priced still counts as priced (one usable variant price is a price) — it does
///     not, by itself, push the recipe into <c>Partial</c>.</item>
///   <item><see cref="CostCompleteness.None"/> — nothing priced; <c>Amount</c> is null
///     (never shown as zero, J3).</item>
/// </list>
/// </summary>
public sealed class CostingService(
    IPriceReader priceReader,
    IUnitConverter unitConverter,
    ICatalogProductReader catalogReader)
{
    /// <summary>
    /// Computes the <see cref="CostPerServing"/> for <paramref name="recipe"/> scaled to
    /// <paramref name="desiredServings"/>. Catalog facts (parent/variant tree) are batch-resolved once;
    /// price reads are performed per price ref (leaf product or live variant), memoized per compute call.
    /// </summary>
    public async Task<CostPerServing> ComputeAsync(
        Recipe recipe,
        int desiredServings,
        CancellationToken ct = default)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        var allProductIds = recipe.Ingredients.Select(i => i.ProductId).Distinct().ToList();
        var catalogById = await catalogReader.FindManyWithVariantsAsync(allProductIds, ct);

        var priceByRef = await ResolvePricesAsync(allProductIds, catalogById, ct);
        var converter = await ResolveConverterAsync(
            recipe.Ingredients.Select(i => (i.ProductId, i.UnitId)), catalogById, priceByRef, ct);

        return ComputeCore(
            recipe.Ingredients.Select(i => (i.ProductId, i.Quantity, i.UnitId)),
            scale, desiredServings, catalogById, priceByRef, converter);
    }

    /// <summary>
    /// Expanded-view costing (recipe-composition.md §7, D4): costs a recipe over the flat
    /// <see cref="EffectiveIngredient"/> set produced by aggregating its expanded lines by
    /// <c>(ProductId, UnitId)</c>, so a parent's cost includes its sub-recipes' ingredient cost × the
    /// inclusion factor by construction (the factor is already baked into each effective line's quantity).
    /// Shares the exact per-line pricing logic with the flat <see cref="ComputeAsync(Recipe,int,CancellationToken)"/>
    /// path, so a flat recipe (expansion is a no-op) yields an identical figure.
    /// </summary>
    /// <param name="lines">The aggregated effective ingredient set (from <see cref="ExpandedLineAggregation.AggregateByProductAndUnit"/>).</param>
    /// <param name="defaultServings">The recipe's default serving count — the denominator of the scale.</param>
    /// <param name="desiredServings">The serving count cost-per-serving is evaluated at.</param>
    public async Task<CostPerServing> ComputeExpandedAsync(
        IReadOnlyList<EffectiveIngredient> lines,
        int defaultServings,
        int desiredServings,
        CancellationToken ct = default)
    {
        var scale = (decimal)desiredServings / defaultServings;

        var allProductIds = lines.Select(l => l.ProductId).Distinct().ToList();
        var catalogById = await catalogReader.FindManyWithVariantsAsync(allProductIds, ct);

        var priceByRef = await ResolvePricesAsync(allProductIds, catalogById, ct);
        var converter = await ResolveConverterAsync(
            lines.Select(l => (l.ProductId, l.UnitId)), catalogById, priceByRef, ct);

        return ComputeCore(
            lines.Select(l => (l.ProductId, l.Quantity, l.UnitId)),
            scale, desiredServings, catalogById, priceByRef, converter);
    }

    /// <summary>
    /// Batch-resolves the effective (deal-aware) price for every price ref (leaf product id, or each
    /// live variant of a parent product, DM-19) the given lines will need — one <see cref="IPriceReader"/>
    /// call per distinct ref, so a variant shared by several lines is fetched once per compute call.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, PricePoint>> ResolvePricesAsync(
        IEnumerable<Guid> lineProductIds,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        CancellationToken ct)
    {
        var refs = new HashSet<Guid>();
        foreach (var productId in lineProductIds)
            foreach (var refId in PriceRefsFor(catalogById, productId))
                refs.Add(refId);

        var result = new Dictionary<Guid, PricePoint>();
        foreach (var refId in refs)
        {
            var price = await priceReader.FindLatestAsync(refId, ct);
            if (price is not null)
                result[refId] = price;
        }

        return result;
    }

    /// <summary>
    /// Pre-resolves every unit conversion the pure rule core will need (one per distinct (price ref,
    /// price unit, line unit) triple) into an in-memory lookup, so the core can run fully synchronously
    /// (ADR-021 rule 1: SQL/async fetches the data, C# keeps the math) — mirrors
    /// <see cref="FulfillmentService.ResolveConverterAsync"/>. A ref with no resolved price needs no
    /// conversion (nothing to convert) and is skipped.
    /// </summary>
    private async Task<Func<Guid, decimal, Guid, Guid, Result<decimal>>> ResolveConverterAsync(
        IEnumerable<(Guid ProductId, Guid? UnitId)> lines,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, PricePoint> priceByRef,
        CancellationToken ct)
    {
        var resolved = new Dictionary<(Guid RefId, Guid FromUnit, Guid ToUnit), Result<decimal>>();
        foreach (var (productId, unitId) in lines)
        {
            if (unitId is null) continue; // untracked line — no conversion needed

            foreach (var refId in PriceRefsFor(catalogById, productId))
            {
                if (!priceByRef.TryGetValue(refId, out var pricePoint)) continue;

                var key = (refId, pricePoint.UnitId, unitId.Value);
                if (resolved.ContainsKey(key)) continue;
                resolved[key] = await unitConverter.ConvertAsync(refId, 1m, pricePoint.UnitId, unitId.Value, ct);
            }
        }

        return (refId, _, fromUnit, toUnit) =>
            resolved.GetValueOrDefault((refId, fromUnit, toUnit), ConversionUnavailable);
    }

    private static readonly Result<decimal> ConversionUnavailable =
        Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No conversion path."));

    /// <summary>
    /// The price refs a line prices from (DM-19): a leaf product prices against itself; a parent product
    /// prices against each of its live variant children. A product id absent from the catalog lookup is
    /// treated as a leaf (self) — preserves current behaviour for unresolvable ids. Single source of
    /// truth for "which price feeds this line", shared by price batching, conversion pre-resolution, and
    /// the rule core — mirrors <see cref="FulfillmentService.StockRefsFor"/>.
    /// </summary>
    private static IReadOnlyList<Guid> PriceRefsFor(
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById, Guid productId) =>
        catalogById.TryGetValue(productId, out var catalogProduct) && catalogProduct.IsParent
            ? catalogProduct.VariantProductIds
            : [productId];

    /// <summary>
    /// Maps a flat set of (productId, quantity, unitId) lines through the shared per-line pricing core
    /// into a <see cref="CostPerServing"/>. Shared verbatim by both async paths and the pure
    /// <see cref="Compute"/> overload (which is handed pre-resolved prices/converter) — so all three
    /// are byte-identical.
    /// </summary>
    private static CostPerServing ComputeCore(
        IEnumerable<(Guid ProductId, decimal? Quantity, Guid? UnitId)> lines,
        decimal scale,
        int desiredServings,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, PricePoint> priceByRef,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
    {
        var costableCount = 0;
        var pricedCount = 0;
        var runningTotal = 0m;
        var missingPriceProductIds = new List<Guid>();

        foreach (var (productId, quantity, unitId) in lines)
        {
            var (costable, lineCost) = ComputeLineCore(
                productId, quantity, unitId, scale, catalogById, priceByRef, converter);
            if (!costable)
                continue; // untracked staple — "no price by design", not a data gap (C12)

            costableCount++;
            if (lineCost.HasValue)
            {
                runningTotal += lineCost.Value;
                pricedCount++;
            }
            else
            {
                // Resolved against ingredient.ProductId itself — for a parent line this is the
                // PARENT's own id, never a variant id (the UI resolves this list against ingredient
                // product ids, D2).
                missingPriceProductIds.Add(productId);
            }
        }

        return BuildResult(runningTotal, pricedCount, costableCount, desiredServings, missingPriceProductIds);
    }

    /// <summary>
    /// The single pure per-line pricing rule core — the one place the pricing rules live (C12 untracked
    /// exclusion, DM-19 parent/variant cheapest-converted-candidate rollup). Keyed only on
    /// product/quantity/unit so it is agnostic to whether the line came from a direct ingredient (flat)
    /// or an aggregated expanded line, and to whether the price/converter data was fetched live (async
    /// paths, pre-resolved) or caller-supplied (pure overload). Returns whether the line is costable (a
    /// tracked real product with a known quantity and unit) and, when costable, the scaled line cost or
    /// null when no price ref yielded a usable, convertible price (un-priced — contributes to
    /// <c>MissingPriceProductIds</c>). Does no IO.
    /// </summary>
    private static (bool Costable, decimal? LineCost) ComputeLineCore(
        Guid productId,
        decimal? quantity,
        Guid? unitId,
        decimal scale,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, PricePoint> priceByRef,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
    {
        // Exclude untracked staples (null Quantity/UnitId → "to taste", no price by design, R5/C12).
        if (quantity is null || unitId is null)
            return (false, null);

        // Cheapest converted candidate across the line's price refs (DM-19): a leaf has exactly one
        // ref (itself); a parent has one ref per live variant. No refs (parent with zero live
        // variants) or no successful candidate → un-priced.
        decimal? bestCostPerLineUnit = null;
        foreach (var refId in PriceRefsFor(catalogById, productId))
        {
            if (!priceByRef.TryGetValue(refId, out var pricePoint))
                continue; // this ref has never been priced

            // Derive the unit price expressed per ONE pricePoint.UnitId (price per kg, per lb, per ea —
            // whatever unit the observation was recorded in). Deliberately NOT pricePoint.UnitPrice:
            // that field is Pricing's normalized price per BASE unit of the dimension (per gram, per
            // ml — see UnitPriceCalculatorAdapter), a different basis than pricePoint.UnitId whenever
            // the observation's unit has FactorToBase != 1 (kg, lb, L, ...). Using it here would need
            // re-basing by that unit's FactorToBase before the conversion below is valid; deriving
            // straight from Price / Quantity is already on the right basis and needs no extra unit
            // metadata (plantry-1oca — this mismatch understated kg/lb-priced ingredients by exactly
            // that factor, e.g. 1000x for kg). UnitPrice remains a display/persistence concern for
            // other readers (e.g. the product detail page) — CostingService never reads it.
            decimal unitPrice;
            if (pricePoint.Quantity > 0m)
            {
                unitPrice = pricePoint.Price / pricePoint.Quantity;
            }
            else
            {
                continue; // degenerate observation (zero/negative quantity) — skip as a candidate
            }

            // unitPrice is expressed in pricePoint.UnitId. Convert 1 unit of pricePoint.UnitId →
            // the line's UnitId to get cost per line-unit. Conversion is keyed on the REF's id (the
            // variant's own conversion bridges, if any) — not the line's productId — since the price
            // and any density bridge belong to the concrete product actually priced.
            var conversionResult = converter(refId, 1m, pricePoint.UnitId, unitId.Value);
            if (!conversionResult.IsSuccess)
                continue; // no conversion path for this ref — skip as a candidate, others may still price the line

            var lineUnitsPerPriceUnit = conversionResult.Value;
            if (lineUnitsPerPriceUnit <= 0m)
                continue;

            var costPerLineUnit = unitPrice / lineUnitsPerPriceUnit;
            if (bestCostPerLineUnit is null || costPerLineUnit < bestCostPerLineUnit.Value)
                bestCostPerLineUnit = costPerLineUnit;
        }

        if (bestCostPerLineUnit is null)
            return (true, null); // costable but un-priced — no ref yielded a usable, convertible price

        var scaledQuantity = quantity.Value * scale;
        return (true, bestCostPerLineUnit.Value * scaledQuantity);
    }

    /// <summary>Assembles the <see cref="CostPerServing"/> from the accumulated per-line tallies (shared by both async paths).</summary>
    private static CostPerServing BuildResult(
        decimal runningTotal, int pricedCount, int costableCount, int desiredServings, List<Guid> missingPriceProductIds)
    {
        CostCompleteness completeness;
        decimal? amount;

        if (pricedCount == 0)
        {
            completeness = CostCompleteness.None;
            amount = null;
        }
        else if (pricedCount == costableCount)
        {
            completeness = CostCompleteness.Full;
            amount = runningTotal / desiredServings;
        }
        else
        {
            completeness = CostCompleteness.Partial;
            amount = runningTotal / desiredServings;
        }

        return new CostPerServing(
            amount,
            completeness,
            pricedCount,
            costableCount,
            missingPriceProductIds);
    }

    /// <summary>
    /// Pure overload: computes the <see cref="CostPerServing"/> for <paramref name="recipe"/> scaled
    /// to <paramref name="desiredServings"/> using <b>only</b> the data already loaded by the caller.
    /// Issues zero further round-trips (ADR-021 rule 1: SQL fetches data, C# keeps the math). Shares the
    /// exact <see cref="ComputeLineCore"/> pricing logic with the async paths (DM-19 parent/variant
    /// rollup included), so a caller pre-loading the same facts gets a byte-identical figure.
    ///
    /// The <paramref name="converter"/> delegate must resolve quantities between units without any IO.
    /// On conversion failure the ref is skipped as a candidate (matching the async path's behaviour).
    /// </summary>
    /// <param name="recipe">The recipe to cost.</param>
    /// <param name="desiredServings">Target serving count.</param>
    /// <param name="catalogById">Pre-loaded product facts keyed by product id — must include every
    /// distinct ingredient product id plus the live variant children of any parent product (DM-19). A
    /// product id absent from this dictionary is treated as a leaf (self).</param>
    /// <param name="priceById">Pre-loaded latest price observations keyed by product id — for a
    /// parent-referencing ingredient this must include prices for its variant children, not just the
    /// parent's own id (which is never itself priced). Refs absent from this dictionary are treated as
    /// un-priced.</param>
    /// <param name="converter">Sync unit conversion delegate: (productId, amount, fromUnitId, toUnitId) → Result.</param>
    public CostPerServing Compute(
        Recipe recipe,
        int desiredServings,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, PricePoint> priceById,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        return ComputeCore(
            recipe.Ingredients.Select(i => (i.ProductId, i.Quantity, i.UnitId)),
            scale, desiredServings, catalogById, priceById, converter);
    }
}

// ── Value objects ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Three-state completeness of a cost-per-serving computation (recipes-domain-model.md §6).
/// </summary>
public enum CostCompleteness
{
    /// <summary>
    /// Every costable ingredient has a price — <c>Amount</c> is exact.
    /// </summary>
    Full,

    /// <summary>
    /// Some costable ingredients have prices, some do not — <c>Amount</c> is a flagged under-estimate.
    /// <c>MissingPriceProductIds</c> lists the un-priced products.
    /// </summary>
    Partial,

    /// <summary>
    /// No costable ingredient has a price — <c>Amount</c> is null and the figure is omitted entirely
    /// (never shown as zero, J3).
    /// </summary>
    None,
}

/// <summary>
/// The cost-per-serving computation result for one recipe at a given serving count
/// (recipes-domain-model.md §6). Never persisted — computed fresh from live Pricing reads (J3).
/// </summary>
/// <param name="Amount">
/// Cost per serving, or null when <see cref="Completeness"/> is <see cref="CostCompleteness.None"/>.
/// When <see cref="CostCompleteness.Partial"/> this is a flagged under-estimate (only priced
/// ingredients contribute). Never shown as zero — a null Amount means "figure omitted" (J3).
/// </param>
/// <param name="Completeness">
/// How complete the costing is, based on how many costable ingredients have price data.
/// </param>
/// <param name="PricedCount">Number of costable ingredients for which a price was found.</param>
/// <param name="CostableCount">
/// Total costable ingredients (tracked, real — excludes untracked staples). Zero when the recipe
/// has no costable ingredients at all.
/// </param>
/// <param name="MissingPriceProductIds">
/// Product ids for which no price observation exists (empty when <see cref="CostCompleteness.Full"/>
/// or <see cref="CostCompleteness.None"/>).
/// </param>
public sealed record CostPerServing(
    decimal? Amount,
    CostCompleteness Completeness,
    int PricedCount,
    int CostableCount,
    IReadOnlyList<Guid> MissingPriceProductIds);
