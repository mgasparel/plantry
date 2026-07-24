using Plantry.Catalog.Domain;

namespace Plantry.Catalog.Application;

/// <summary>
/// Shared unit-dropdown ordering (plantry-n9iw). Every unit &lt;select&gt; across the app groups
/// options by <see cref="Dimension"/> in the enum's own declaration order — Mass -&gt; Volume -&gt;
/// Count (<c>Dimension.cs:4-9</c>) — the same order <c>IUnitRepository.ListAsync</c> already emits
/// (<c>ORDER BY Dimension, Name</c>), with each group internally ordered by <see cref="Unit.Code"/>.
/// Returns plain <see cref="Unit"/> instances rather than <c>SelectListItem</c>s: Application must not
/// depend on ASP.NET Core (enforced by
/// <c>Catalog_Application_Should_Not_Reference_Infrastructure_Packages</c>) — callers hand the ordered
/// sequence to <c>Plantry.Web.Pages.Shared.UnitSelectListBuilder</c> for the actual optgroup
/// construction, or reuse this ordering before projecting into their own anti-corruption-layer DTO
/// (e.g. <c>ShoppingUnitOption</c>, <c>CatalogUnitOption</c>) so order survives the ACL boundary.
/// </summary>
public static class UnitQueries
{
    public static IReadOnlyList<Unit> OrderForDropdown(IEnumerable<Unit> units) =>
        units
            .OrderBy(u => u.Dimension)
            .ThenBy(u => u.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
