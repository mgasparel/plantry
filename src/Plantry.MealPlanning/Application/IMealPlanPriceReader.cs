namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto Pricing for the MealPlanning context (domain-model §8, DM-17).
/// Reuses the same minimal contract as <c>Plantry.Recipes.Application.IPriceReader</c> but is
/// owned by MealPlanning.Application to keep MealPlanning free of Recipes dependencies (DM-3).
/// Implemented in Plantry.Web over the same <c>PricingQueries</c> adapter.
/// Deal-aware (P5-9b, DJ6): the Web adapter reads Pricing's effective-price read model — the cheapest
/// active in-window deal when one exists, else the latest purchase — so cost/weighting reflect live
/// sales without MealPlanning ever depending on Deals (ADR-010).
/// </summary>
public interface IMealPlanPriceReader
{
    /// <summary>
    /// Returns the effective (deal-aware) price observation for a product, or null when no price has
    /// been recorded. The price covers <see cref="MealPlanPricePoint.Quantity"/> units.
    /// </summary>
    Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default);
}

/// <summary>
/// Minimal price-point fact for one product, sufficient to compute a cost estimate.
/// Mirrors <c>Plantry.Recipes.Application.PricePoint</c> — a separate copy per context (DM-3).
/// </summary>
/// <param name="ProductId">The product this price covers.</param>
/// <param name="Price">Total price paid.</param>
/// <param name="Quantity">Quantity purchased (in <see cref="UnitId"/>).</param>
/// <param name="UnitId">Unit of the observation.</param>
/// <param name="UnitPrice">
/// Pre-computed price per single unit when available; prefer this over recomputing from Price/Quantity.
/// </param>
public sealed record MealPlanPricePoint(
    Guid ProductId,
    decimal Price,
    decimal Quantity,
    Guid UnitId,
    decimal? UnitPrice);
