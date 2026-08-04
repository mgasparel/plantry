namespace Plantry.Planning.Application;

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
/// Pricing's pre-computed price per BASE unit of the dimension (per gram, per ml — see
/// <c>UnitPriceCalculatorAdapter</c>: <c>price / (quantity × unit.FactorToBase)</c>), if the
/// normalisation succeeded (soft-fail per pricing.md resolved-call #2 — null means normalisation
/// failed, not that the price is zero). This is <b>not</b> price per <see cref="UnitId"/> whenever
/// that unit's <c>FactorToBase != 1</c> (kg, lb, L, ...) — it is a different, larger unit basis.
/// <see cref="Plantry.Planning.Domain.PlanCostingService"/> does not use this field for costing
/// math (it derives cost from <see cref="Price"/> / <see cref="Quantity"/>, converted to the
/// product's default unit); treat it as a display/persistence concern for other readers
/// (plantry-9n7l — mirrors plantry-1oca's fix to <c>Plantry.Recipes.Application.PricePoint</c>).
/// </param>
public sealed record MealPlanPricePoint(
    Guid ProductId,
    decimal Price,
    decimal Quantity,
    Guid UnitId,
    decimal? UnitPrice);
