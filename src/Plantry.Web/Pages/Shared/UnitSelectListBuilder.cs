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
        Func<Unit, string> text)
    {
        var groups = new Dictionary<Dimension, SelectListGroup>();
        return UnitQueries.OrderForDropdown(units)
            .Select(u => new SelectListItem(text(u), value(u))
            {
                Group = GroupFor(groups, u.Dimension),
            })
            .ToList();
    }

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
        Func<T, string> sortKey)
    {
        var groups = new Dictionary<Dimension, SelectListGroup>();
        return units
            .OrderBy(dimension)
            .ThenBy(sortKey, StringComparer.OrdinalIgnoreCase)
            .Select(u => new SelectListItem(text(u), value(u))
            {
                Group = GroupFor(groups, dimension(u)),
            })
            .ToList();
    }

    /// <summary>
    /// Returns the same <see cref="SelectListGroup"/> instance for every item in a given
    /// <see cref="Dimension"/>, rather than a fresh instance per item. This matters beyond cosmetics:
    /// ASP.NET Core's own <c>&lt;select asp-items="..."&gt;</c> tag helper
    /// (<c>DefaultHtmlGenerator.GenerateGroupsAndOptions</c>) wraps consecutive items into the same
    /// &lt;optgroup&gt; only when their <see cref="SelectListItem.Group"/> objects are
    /// <c>ReferenceEquals</c> — it does not compare <see cref="SelectListGroup.Name"/>. A fresh
    /// <c>new SelectListGroup { Name = ... }</c> per item (the previous behaviour here) therefore
    /// produced one &lt;optgroup&gt; per option even when every option shared the same dimension.
    /// </summary>
    private static SelectListGroup GroupFor(Dictionary<Dimension, SelectListGroup> groups, Dimension dimension)
    {
        if (!groups.TryGetValue(dimension, out var group))
        {
            group = new SelectListGroup { Name = dimension.ToString() };
            groups[dimension] = group;
        }

        return group;
    }
}
