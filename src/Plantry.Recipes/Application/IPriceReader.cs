namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto Pricing (recipes-domain-model.md §8; DM-17). Returns the
/// effective (deal-aware) <c>PriceObservation</c> for a product — the price (in the observation's
/// unit) that <see cref="Plantry.Recipes.Domain.CostingService"/> uses to compute cost-per-serving.
/// Deal-aware (P5-9b, DJ6): the Web adapter reads Pricing's effective-price read model — cheapest active
/// in-window deal else latest purchase — so cost reflects live sales without Recipes depending on Deals.
/// Defined here in Recipes.Application and <b>implemented in Plantry.Web</b> over
/// <see cref="Plantry.Pricing.Application.PricingQueries"/>, so the Recipes projects keep their
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
/// Pre-computed price per single unit, if the Pricing context computed it successfully (soft-fail
/// per pricing.md resolved-call #2 — null means the normalisation failed, not that the price is
/// zero). When non-null, prefer this over re-deriving from Price / Quantity.
/// </param>
public sealed record PricePoint(
    Guid ProductId,
    decimal Price,
    decimal Quantity,
    Guid UnitId,
    decimal? UnitPrice);
