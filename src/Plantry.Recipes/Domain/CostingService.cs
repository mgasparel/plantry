using Plantry.Recipes.Application;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Domain service that computes the cost-per-serving for a recipe at a given serving count
/// (recipes-domain-model.md §7). Stateless; reads price history via <see cref="IPriceReader"/>
/// and converts units via <see cref="IUnitConverter"/>. Never persisted — computed fresh at
/// query time (J3, recipes-domain-model.md §1).
///
/// Costable set: tracked, real (non-untracked-staple) ingredients only. Untracked staples
/// (<c>track_stock = false</c>, null Quantity/UnitId) are excluded from <c>CostableCount</c>
/// because "no price by design" is not a data gap (C12).
///
/// Completeness rules (recipes-domain-model.md §6 / CostCompleteness):
/// <list type="bullet">
///   <item><see cref="CostCompleteness.Full"/> — every costable ingredient has a price
///     (<c>PricedCount == CostableCount &gt; 0</c>); <c>Amount</c> is exact.</item>
///   <item><see cref="CostCompleteness.Partial"/> — some priced, some not
///     (<c>0 &lt; PricedCount &lt; CostableCount</c>); <c>Amount</c> is a flagged under-estimate
///     and <c>MissingPriceProductIds</c> lists the un-priced products.</item>
///   <item><see cref="CostCompleteness.None"/> — nothing priced; <c>Amount</c> is null
///     (never shown as zero, J3).</item>
/// </list>
/// </summary>
public sealed class CostingService(IPriceReader priceReader, IUnitConverter unitConverter)
{
    /// <summary>
    /// Computes the <see cref="CostPerServing"/> for <paramref name="recipe"/> scaled to
    /// <paramref name="desiredServings"/>. All price reads are performed per-ingredient; a null
    /// price observation is treated as an un-priced line (contributes to <c>MissingPriceProductIds</c>,
    /// not to the running total).
    /// </summary>
    public async Task<CostPerServing> ComputeAsync(
        Recipe recipe,
        int desiredServings,
        CancellationToken ct = default)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        var costableCount = 0;
        var pricedCount = 0;
        var runningTotal = 0m;
        var missingPriceProductIds = new List<Guid>();

        foreach (var ingredient in recipe.Ingredients)
        {
            var (costable, lineCost) = await CostLineAsync(
                ingredient.ProductId, ingredient.Quantity, ingredient.UnitId, scale, ct);
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
                missingPriceProductIds.Add(ingredient.ProductId);
            }
        }

        return BuildResult(runningTotal, pricedCount, costableCount, desiredServings, missingPriceProductIds);
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

        var costableCount = 0;
        var pricedCount = 0;
        var runningTotal = 0m;
        var missingPriceProductIds = new List<Guid>();

        foreach (var line in lines)
        {
            var (costable, lineCost) = await CostLineAsync(line.ProductId, line.Quantity, line.UnitId, scale, ct);
            if (!costable)
                continue;

            costableCount++;
            if (lineCost.HasValue)
            {
                runningTotal += lineCost.Value;
                pricedCount++;
            }
            else
            {
                missingPriceProductIds.Add(line.ProductId);
            }
        }

        return BuildResult(runningTotal, pricedCount, costableCount, desiredServings, missingPriceProductIds);
    }

    /// <summary>
    /// Core per-line pricing shared by the flat and expanded async paths. Returns whether the line is
    /// costable (a tracked real product with a known quantity and unit — untracked staples are not, C12) and,
    /// when costable, the scaled line cost or null when the line could not be priced (no price observation,
    /// degenerate observation, or no unit-conversion path — each treated as un-priced, contributing to
    /// <c>MissingPriceProductIds</c>).
    /// </summary>
    private async Task<(bool Costable, decimal? LineCost)> CostLineAsync(
        Guid productId, decimal? quantity, Guid? unitId, decimal scale, CancellationToken ct)
    {
        // Exclude untracked staples (null Quantity/UnitId → "to taste", no price by design, R5/C12).
        if (quantity is null || unitId is null)
            return (false, null);

        var pricePoint = await priceReader.FindLatestAsync(productId, ct);
        if (pricePoint is null)
            return (true, null); // costable but un-priced

        // Derive the unit price (price per one unit). Prefer the Pricing-computed UnitPrice; otherwise
        // derive from Price / Quantity (guarded against zero).
        decimal unitPrice;
        if (pricePoint.UnitPrice.HasValue)
        {
            unitPrice = pricePoint.UnitPrice.Value;
        }
        else if (pricePoint.Quantity > 0m)
        {
            unitPrice = pricePoint.Price / pricePoint.Quantity;
        }
        else
        {
            // Degenerate observation (zero quantity, no UnitPrice): treat as un-priced.
            return (true, null);
        }

        // The unit price is expressed in pricePoint.UnitId. Convert 1 unit of pricePoint.UnitId →
        // the line's UnitId to get cost per line-unit, then multiply by the scaled required quantity.
        // cost/lineUnit = unitPrice / (lineUnits per priceUnit). Example: priceUnit = g, lineUnit = kg;
        // convert(1 g → kg) = 0.001; unitPrice = $0.002/g → cost per kg = $0.002 / 0.001 = $2/kg. ✓
        var conversionResult = await unitConverter.ConvertAsync(productId, 1m, pricePoint.UnitId, unitId.Value, ct);
        if (!conversionResult.IsSuccess)
            return (true, null); // no conversion path → un-priced

        var lineUnitsPerPriceUnit = conversionResult.Value;
        if (lineUnitsPerPriceUnit <= 0m)
            return (true, null);

        var costPerLineUnit = unitPrice / lineUnitsPerPriceUnit;
        var scaledQuantity = quantity.Value * scale;
        return (true, costPerLineUnit * scaledQuantity);
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
    /// Issues zero further round-trips (ADR-021 rule 1: SQL fetches data, C# keeps the math).
    ///
    /// The <paramref name="converter"/> delegate must resolve quantities between units without any IO.
    /// On conversion failure the ingredient is treated as un-priced (contributes to
    /// <c>MissingPriceProductIds</c>), matching the async path's behaviour.
    /// </summary>
    /// <param name="recipe">The recipe to cost.</param>
    /// <param name="desiredServings">Target serving count.</param>
    /// <param name="priceById">Pre-loaded latest price observations keyed by product id.
    /// Products absent from this dictionary are treated as un-priced.</param>
    /// <param name="converter">Sync unit conversion delegate: (productId, amount, fromUnitId, toUnitId) → Result.</param>
    public CostPerServing Compute(
        Recipe recipe,
        int desiredServings,
        IReadOnlyDictionary<Guid, PricePoint> priceById,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        var costableCount = 0;
        var pricedCount = 0;
        var runningTotal = 0m;
        var missingPriceProductIds = new List<Guid>();

        foreach (var ingredient in recipe.Ingredients)
        {
            // Exclude untracked staples (null Quantity/UnitId → "to taste", no price by design).
            if (ingredient.Quantity is null || ingredient.UnitId is null)
                continue;

            costableCount++;

            if (!priceById.TryGetValue(ingredient.ProductId, out var pricePoint))
            {
                missingPriceProductIds.Add(ingredient.ProductId);
                continue;
            }

            // Derive unit price (price per one unit of pricePoint.UnitId).
            decimal unitPrice;
            if (pricePoint.UnitPrice.HasValue)
            {
                unitPrice = pricePoint.UnitPrice.Value;
            }
            else if (pricePoint.Quantity > 0m)
            {
                unitPrice = pricePoint.Price / pricePoint.Quantity;
            }
            else
            {
                missingPriceProductIds.Add(ingredient.ProductId);
                continue;
            }

            // Convert 1 priceUnit → ingredientUnit to get cost per ingredient-unit.
            var conversionResult = converter(
                ingredient.ProductId,
                1m,
                pricePoint.UnitId,
                ingredient.UnitId.Value);

            if (!conversionResult.IsSuccess)
            {
                missingPriceProductIds.Add(ingredient.ProductId);
                continue;
            }

            var ingredientUnitsPerPriceUnit = conversionResult.Value;
            if (ingredientUnitsPerPriceUnit <= 0m)
            {
                missingPriceProductIds.Add(ingredient.ProductId);
                continue;
            }

            var costPerIngredientUnit = unitPrice / ingredientUnitsPerPriceUnit;
            var scaledQuantity = ingredient.Quantity.Value * scale;
            var lineCost = costPerIngredientUnit * scaledQuantity;

            runningTotal += lineCost;
            pricedCount++;
        }

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
