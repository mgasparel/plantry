using Plantry.Catalog.Application;
using Plantry.Identity.Application;

namespace Plantry.Web.Inventory;

/// <summary>
/// Composition-root adapter for <see cref="IHouseholdExpiryDefaultsReader"/> — delegates to Identity's
/// <see cref="IHouseholdExpiryDefaults"/>, the single source of truth for the per-household freeze/thaw
/// expiry defaults (plantry-hh1f), through the per-request <see cref="HouseholdExpiryDefaultsAccessor"/>
/// (plantry-hw39) rather than calling <see cref="IHouseholdExpiryDefaults.GetAsync"/> directly — this
/// port's only consumer, <see cref="CatalogReadFacade"/>, is called per-product in a loop by
/// <c>InventoryStockReaderAdapter</c>, so reading straight through would issue one <c>households</c>
/// SELECT per product instead of one per request. Lives in Plantry.Composition, the composition root
/// that references both contexts, so the Catalog project stays <c>→ SharedKernel only</c> and never
/// takes a hard dependency on Identity (ADR-002, Gate 2) — mirroring <c>AiAssistanceGateReaderAdapter</c>.
///
/// <para>Namespaced <c>Plantry.Web.Inventory</c> rather than the more conventional
/// <c>Plantry.Web.Catalog</c> (folder = the port-owning context, MealPlanning/Recipes-adapter
/// convention): a sibling <c>Plantry.Web.Catalog</c> namespace would shadow the <c>Catalog</c> segment
/// of <c>Plantry.Catalog.Domain</c>/<c>Plantry.Catalog.Application</c> for every file in
/// <c>Plantry.Web.Inventory</c> that spells it out unqualified (e.g.
/// <c>TakeStockReaderAdapter</c>'s <c>Catalog.Domain.ProductId.From</c>) — C# resolves an unqualified
/// leading segment against a sibling namespace before the top-level one. <see cref="CatalogReadFacade"/>,
/// this port's only consumer today, already lives in this namespace, so this is also where the
/// adapter is used.</para>
/// </summary>
public sealed class HouseholdExpiryDefaultsReaderAdapter(HouseholdExpiryDefaultsAccessor defaults)
    : IHouseholdExpiryDefaultsReader
{
    public async Task<(int AfterFreezing, int AfterThawing)> GetDefaultsAsync(CancellationToken ct = default) =>
        await defaults.GetAsync(ct);
}
