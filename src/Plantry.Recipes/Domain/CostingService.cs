using Plantry.Recipes.Application;

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
            // Exclude untracked staples (null Quantity/UnitId → "to taste", no price by design).
            // Costable = tracked real product with a known quantity and unit (R5).
            if (ingredient.Quantity is null || ingredient.UnitId is null)
                continue;

            costableCount++;

            var pricePoint = await priceReader.FindLatestAsync(ingredient.ProductId, ct);
            if (pricePoint is null)
            {
                missingPriceProductIds.Add(ingredient.ProductId);
                continue;
            }

            // Derive the unit price (price per one unit) from the observation.
            // Use UnitPrice when the Pricing context already computed it (preferred); otherwise
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
                missingPriceProductIds.Add(ingredient.ProductId);
                continue;
            }

            // The unit price is expressed in pricePoint.UnitId. Convert to the ingredient's
            // UnitId so we can multiply by the scaled required quantity.
            // We convert 1 unit of pricePoint.UnitId → ingredient.UnitId to get cost/ingredient-unit.
            var conversionResult = await unitConverter.ConvertAsync(
                ingredient.ProductId,
                1m,
                pricePoint.UnitId,
                ingredient.UnitId.Value,
                ct);

            if (!conversionResult.IsSuccess)
            {
                // No unit conversion path → treat as un-priced for this line.
                missingPriceProductIds.Add(ingredient.ProductId);
                continue;
            }

            // pricePerIngredientUnit = unitPrice × (1 priceUnit expressed in ingredientUnits)
            // Wait — we want cost per ingredient-unit. The unit price is price/priceUnit.
            // cost/ingredientUnit = price/priceUnit × (priceUnits/ingredientUnit)
            //   = unitPrice / (ingredientUnits per priceUnit)
            //   = unitPrice × (priceUnits per ingredientUnit)
            // ConvertAsync(productId, 1m, priceUnitId, ingredientUnitId) gives us
            //   how many ingredient-units 1 price-unit equals.
            // So cost per ingredient-unit = unitPrice / conversionResult.Value.
            // Example: priceUnit = g, ingredientUnit = kg; convert(1 g → kg) = 0.001.
            // unitPrice = $0.002/g → cost per kg = $0.002 / 0.001 = $2/kg. ✓
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

        // Determine completeness and build result.
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
