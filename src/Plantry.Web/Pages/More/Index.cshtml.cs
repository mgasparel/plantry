using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Housekeeping.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.More;

[Authorize]
public sealed class IndexModel(ITidyUpBadgeCache tidyUpBadgeCache, ITenantContext tenant) : PageModel
{
    /// <summary>Cached open-finding count for the Tidy Up tile (T7) — read-only, never runs a detector.</summary>
    public int TidyUpCount { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is { } householdId)
            TidyUpCount = await tidyUpBadgeCache.TryGetAsync(HouseholdId.From(householdId), ct) ?? 0;
    }
}
