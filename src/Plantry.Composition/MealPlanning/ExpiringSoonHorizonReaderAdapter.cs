using Plantry.Inventory.Application;
using Plantry.MealPlanning.Application;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for MealPlanning's <see cref="IExpiringSoonHorizonReader"/> — delegates to
/// Inventory's <see cref="IExpiringSoonHorizon"/>, the single source of truth for the per-household
/// "expiring soon" horizon. Lives in Plantry.Web, the composition root that references both contexts,
/// so the MealPlanning project stays <c>→ SharedKernel only</c> and never touches Inventory's EF
/// context directly (ADR-002). The meal-plan roll-up "use soon" flag and the weekly insights engine
/// therefore resolve the exact same value as Inventory's Today expiring-soon widget and the Recipes
/// per-cell fulfillment (plantry-5yhd).
/// </summary>
public sealed class ExpiringSoonHorizonReaderAdapter(IExpiringSoonHorizon horizon)
    : IExpiringSoonHorizonReader
{
    public Task<int> GetDaysAsync(CancellationToken ct = default) =>
        horizon.GetDaysAsync(ct);
}
