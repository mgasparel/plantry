using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.TidyUp;

/// <summary>
/// The Tidy Up page (tidy-up.md §5/T2): one card per detector with open findings, a flat dismissed
/// disclosure, and the "All tidy" empty state. Dismiss/Restore are htmx posts that swap the whole
/// <c>#tidyup-body</c> region plus the nav badge out-of-band (T1/T6), the same fragment pattern as
/// Shopping's <c>_AddPostResult</c>.
/// </summary>
[Authorize]
public sealed class IndexModel(
    GetTidyUpPageQuery getTidyUpPage,
    DismissFindingCommand dismissFinding,
    RestoreFindingCommand restoreFinding,
    ITenantContext tenant,
    IClock clock)
    : PageModel
{
    /// <summary>
    /// The computed read model. Named <c>Result</c> rather than <c>Page</c> so it neither shadows
    /// <see cref="PageModel.Page"/> nor collides with Razor's reserved <c>@@page</c> directive token
    /// when referenced from the views as <c>@@Model.Result</c>.
    /// </summary>
    public TidyUpPageResult Result { get; private set; } = TidyUpPageResult.Empty;

    /// <summary>Exposed so the view can render relative dismissal timestamps against a single "now".</summary>
    public DateTimeOffset NowUtc => clock.UtcNow;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Result = await getTidyUpPage.ExecuteAsync(ct);
    }

    public async Task<IActionResult> OnPostDismissAsync(
        string detectorId, Guid subjectId, string fingerprint, CancellationToken ct)
    {
        if (tenant.HouseholdId is { } householdId)
        {
            await dismissFinding.ExecuteAsync(
                HouseholdId.From(householdId), new DetectorId(detectorId), subjectId, fingerprint, ct);
        }

        Result = await getTidyUpPage.ExecuteAsync(ct);
        return Partial("_DismissResult", this);
    }

    public async Task<IActionResult> OnPostRestoreAsync(string detectorId, Guid subjectId, CancellationToken ct)
    {
        if (tenant.HouseholdId is { } householdId)
        {
            await restoreFinding.ExecuteAsync(HouseholdId.From(householdId), new DetectorId(detectorId), subjectId, ct);
        }

        Result = await getTidyUpPage.ExecuteAsync(ct);
        return Partial("_DismissResult", this);
    }
}
