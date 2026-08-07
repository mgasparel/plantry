namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto Pricing (recipes-domain-model.md §8; DM-17). Returns the
/// effective (deal-aware) <c>PriceObservation</c> for a product — the price (in the observation's
/// unit) that <see cref="Plantry.Recipes.Domain.CostingService"/> uses to compute cost-per-serving.
/// Deal-aware (P5-9b, DJ6): the Web adapter reads Pricing's effective-price read model — cheapest active
/// in-window deal else latest purchase — so cost reflects live sales without Recipes depending on Deals.
/// Defined here in Recipes.Application and <b>implemented in Plantry.Web</b> over
/// <see cref="Plantry.Market.Application.PricingQueries"/>, so the Recipes projects keep their
/// <c>→ SharedKernel only</c> dependency. All identifiers cross as raw <see cref="Guid"/> soft refs
/// (DM-3).
/// </summary>
public interface IPriceReader
{
    /// <summary>
    /// Returns the effective (deal-aware) price observation for the given product, or null when no price
    /// has ever been recorded for this product in the household. The returned <see cref="PricePoint"/>
    /// expresses the price per <see cref="PricePoint.Quantity"/> <see cref="PricePoint.UnitId"/>
    /// of the product — <c>CostingService</c> converts to the ingredient unit via
    /// <see cref="IUnitConverter"/> before summing.
    /// </summary>
    Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Batch counterpart to <see cref="FindLatestAsync"/> (plantry-hbol): resolves the effective price for
    /// every given product in a small constant number of round trips instead of one per product —
    /// <see cref="Plantry.Recipes.Domain.CostingService"/> uses this to price all of a recipe's price refs
    /// at once. Products with no recorded price are simply absent from the result. Defaults to a per-id
    /// <see cref="FindLatestAsync"/> loop so existing test doubles need not implement it; the real Web
    /// adapter overrides it with a batched query.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, PricePoint>> FindLatestManyAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, PricePoint>();
        foreach (var productId in productIds.Distinct())
        {
            var price = await FindLatestAsync(productId, ct);
            if (price is not null)
                result[productId] = price;
        }
        return result;
    }
}

/// <summary>
/// The slice of a <c>PriceObservation</c> that <c>CostingService</c> needs: the price paid for
/// a known quantity in a known unit. <see cref="UnitPrice"/> is used when available (already
/// normalised to one unit by the Pricing context); otherwise <c>CostingService</c> derives cost
/// from <see cref="Price"/> / <see cref="Quantity"/> itself.
/// </summary>
/// <param name="ProductId">The product this price observation covers.</param>
/// <param name="Price">Total price paid (e.g. $3.49).</param>
/// <param name="Quantity">Number of units purchased (e.g. 500 for 500 g).</param>
/// <param name="UnitId">Unit in which <see cref="Quantity"/> is expressed.</param>
/// <param name="UnitPrice">
/// Pricing's pre-computed price per BASE unit of the dimension (per gram, per ml — see
/// <c>UnitPriceCalculatorAdapter</c>: <c>price / (quantity × unit.FactorToBase)</c>), if the
/// normalisation succeeded (soft-fail per pricing.md resolved-call #2 — null means normalisation
/// failed, not that the price is zero). This is <b>not</b> price per <see cref="UnitId"/> whenever
/// that unit's <c>FactorToBase != 1</c> (kg, lb, L, ...) — it is a different, larger unit basis.
/// <see cref="Plantry.Recipes.Domain.CostingService"/> does not use this field for costing math (it
/// derives cost from <see cref="Price"/> / <see cref="Quantity"/>, which is already expressed per
/// <see cref="UnitId"/> and matches its unit-conversion pipeline); treat it as a display/persistence
/// concern for other readers (plantry-1oca).
/// </param>
public sealed record PricePoint(
    Guid ProductId,
    decimal Price,
    decimal Quantity,
    Guid UnitId,
    decimal? UnitPrice);
