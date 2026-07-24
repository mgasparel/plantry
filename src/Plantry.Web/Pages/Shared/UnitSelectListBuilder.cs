using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;

namespace Plantry.Web.Pages.Shared;

/// <summary>
/// Builds a dimension-grouped &lt;optgroup&gt; <see cref="SelectListItem"/> list for a unit dropdown
/// (plantry-n9iw), so every unit &lt;select&gt; in the app shares one construction point instead of
/// nine near-identical <c>.Select(u =&gt; new SelectListItem(...))</c> blocks. Group names are the raw
/// <see cref="Dimension"/> enum names verbatim ("Mass"/"Volume"/"Count"), matching
/// <c>Catalog/Units/Index.cshtml</c>'s existing grouping headings.
/// </summary>
public static class UnitSelectListBuilder
{
    /// <summary>
    /// For the 6 call sites that already hold live <see cref="Unit"/> instances (a direct
    /// <c>IUnitRepository</c> dependency). Delegates ordering to
    /// <see cref="UnitQueries.OrderForDropdown"/> — the canonical Mass -&gt; Volume -&gt; Count,
    /// then-Code rule — so it isn't re-derived here.
    /// </summary>
    public static List<SelectListItem> BuildFromUnits(
        IEnumerable<Unit> units,
        Func<Unit, string> value,
        Func<Unit, string> text) =>
        UnitQueries.OrderForDropdown(units)
            .Select(u => new SelectListItem(text(u), value(u))
            {
                Group = new SelectListGroup { Name = u.Dimension.ToString() },
            })
            .ToList();

    /// <summary>
    /// For the 3 call sites that go through an anti-corruption-layer DTO
    /// (<c>ShoppingUnitOption</c>, <c>CatalogUnitOption</c>) rather than a live <see cref="Unit"/>.
    /// Applies the same Mass -&gt; Volume -&gt; Count, then-Code rule as
    /// <see cref="UnitQueries.OrderForDropdown"/> directly to the DTO's own fields, since a DTO can't
    /// be handed to that <see cref="Unit"/>-typed method — keep the two in sync if the rule ever changes.
    /// </summary>
    public static List<SelectListItem> Build<T>(
        IEnumerable<T> units,
        Func<T, string> value,
        Func<T, string> text,
        Func<T, Dimension> dimension,
        Func<T, string> sortKey) =>
        units
            .OrderBy(dimension)
            .ThenBy(sortKey, StringComparer.OrdinalIgnoreCase)
            .Select(u => new SelectListItem(text(u), value(u))
            {
                Group = new SelectListGroup { Name = dimension(u).ToString() },
            })
            .ToList();
}
