using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Intake;

/// <summary>
/// Post-commit Done/success screen (SPEC §2e). Redirected here from <see cref="ReviewModel.OnPostCommitAsync"/>
/// after a successful commit. Renders a 4-up summary (items added, stocked value, categories, soonest expiry)
/// derived from the committed session lines via <see cref="GetCommittedSessionSummaryQuery"/>.
///
/// <para>Guard: if the session is not Committed, or belongs to a different household, redirects to Pantry.
/// The committed session is no longer Ready, so a non-committed/foreign session must never render this page.</para>
/// </summary>
[Authorize]
public sealed class DoneModel(
    IImportSessionRepository sessions,
    ITenantContext tenant) : PageModel
{
    /// <summary>Session id from the route; bound on GET.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public CommittedSessionSummary Summary { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var result = await new GetCommittedSessionSummaryQuery(
            ImportSessionId.From(Id), sessions, tenant).ExecuteAsync(ct);

        if (result.IsFailure)
        {
            // Non-committed, not found, or foreign session: send user to Pantry.
            return RedirectToPage("/Pantry/Index");
        }

        Summary = result.Value;
        return Page();
    }
}
