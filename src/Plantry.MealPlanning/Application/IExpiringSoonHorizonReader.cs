namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto the household's "expiring soon" horizon (owned by Inventory,
/// plantry-5yhd). Lets the MealPlanning context flag planned stock as "use soon" — both the
/// plan-level roll-up (<c>PlanFulfillmentService</c>) and the weekly insights engine
/// (<c>PlanInsightsService</c>) — against the <b>same</b> per-household threshold that drives
/// Inventory's Today widget and the Recipes per-cell fulfillment, without coupling MealPlanning to
/// Inventory's domain model or EF context (ADR-002). Defined here in MealPlanning.Application beside
/// <see cref="IMealPlanExpiringStockReader"/> (the existing MealPlanning → Inventory ACL) and
/// <b>implemented in Plantry.Web</b> over Inventory's <c>IExpiringSoonHorizon</c>, so the MealPlanning
/// project keeps its <c>→ SharedKernel only</c> dependency. Mirrors
/// <c>Plantry.Recipes.Application.IExpiringSoonHorizonReader</c> — a separate interface copy per
/// context (DM-3).
/// </summary>
public interface IExpiringSoonHorizonReader
{
    /// <summary>
    /// Returns the current household's "expiring soon" horizon in days, falling back to the Inventory
    /// default when unset. Read once at the IO boundary and threaded into the pure roll-up /
    /// insights computations (ADR-021).
    /// </summary>
    Task<int> GetDaysAsync(CancellationToken ct = default);
}
