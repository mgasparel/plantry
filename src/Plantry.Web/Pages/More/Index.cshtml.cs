using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Hosting;
using Plantry.Housekeeping.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Housekeeping;

namespace Plantry.Web.Pages.More;

[Authorize]
public sealed class IndexModel(
    ITidyUpBadgeCache tidyUpBadgeCache, TidyUpBadgeRefresher tidyUpBadgeRefresher, ITenantContext tenant,
    IHostEnvironment env)
    : PageModel
{
    /// <summary>Cached open-finding count for the Tidy Up tile (T7) — read-only, never runs a detector.</summary>
    public int TidyUpCount { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is not { } householdGuid) return;

        var householdId = HouseholdId.From(householdGuid);
        // SWR (plantry-h0qq): a miss/stale read still shows the last known count; either case asks for a
        // single-flight background recompute rather than trusting a stale number indefinitely. Skipped
        // under the "Testing" WAF host — see the matching note in _Layout.cshtml for why, and
        // TidyUpBadgeSwrIntegrationTests for where the wiring itself is proven.
        var snapshot = await tidyUpBadgeCache.TryGetAsync(householdId, ct);
        TidyUpCount = snapshot?.Count ?? 0;
        if ((snapshot is null || !snapshot.IsFresh) && !env.IsEnvironment("Testing"))
            await tidyUpBadgeRefresher.RequestRefreshAsync(householdId, ct);
    }
}
