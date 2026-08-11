using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Pantry.Application;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Pages.Pantry.TakeStock;

/// <summary>
/// Location list for the Take Stock flow (P4-4b, J1). Reads the household's active locations
/// via <see cref="ITakeStockReader"/> and determines whether the "No location" group should appear.
/// </summary>
[Authorize]
public sealed class IndexModel(ITakeStockReader reader, IClock clock) : PageModel
{
    public IReadOnlyList<TakeStockLocationRow> Locations { get; private set; } = [];

    /// <summary>True when any tracked product has neither active lots nor a default location (J7).</summary>
    public bool HasNoLocationProducts { get; private set; }

    /// <summary>Captured once per request so every card's freshness line (plantry-hp67) is computed
    /// against the same "now" rather than drifting across the loop.</summary>
    public DateTimeOffset NowUtc { get; private set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        NowUtc = clock.UtcNow;
        Locations = await reader.ListLocationsAsync(ct);
        var noLoc = await reader.ListNoLocationRowsAsync(ct);
        HasNoLocationProducts = noLoc.Count > 0;
    }

    /// <summary>
    /// Returns the location list fragment (htmx target <c>#take-stock-list</c>) — supports
    /// future htmx refreshes without a full page reload.
    /// </summary>
    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnGetListAsync(CancellationToken ct = default)
    {
        await OnGetAsync(ct);
        return Partial("_LocationList", this);
    }
}
