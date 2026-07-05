using Plantry.Inventory.Application;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IExpiringSoonHorizonReader"/> — delegates to Inventory's
/// <see cref="IExpiringSoonHorizon"/>, the single source of truth for the per-household "expiring
/// soon" horizon. Lives in Plantry.Web, the composition root that references both contexts, so the
/// Recipes project stays <c>→ SharedKernel only</c> and never touches Inventory's EF context
/// directly (ADR-002). The Recipes browse "use soon" filter therefore resolves the exact same value
/// as Inventory's Today expiring-soon widget (plantry-5yhd).
/// </summary>
public sealed class ExpiringSoonHorizonReaderAdapter(IExpiringSoonHorizon horizon)
    : IExpiringSoonHorizonReader
{
    public Task<int> GetDaysAsync(CancellationToken ct = default) =>
        horizon.GetDaysAsync(ct);
}
