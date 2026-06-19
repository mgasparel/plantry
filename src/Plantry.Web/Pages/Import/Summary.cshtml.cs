using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Migration.Grocy;

namespace Plantry.Web.Pages.Import;

/// <summary>
/// Grocy import pipeline — Pre-commit summary page (plantry-zcw.8).
///
/// GET  — runs <see cref="ImportSummaryService.ComputeAsync"/> (read-only, no Plantry writes)
///         and renders entity counts + the full §8 tradeoff log.
///
/// Query parameters:
///   dryRun=true — renders a "dry run" banner and hides commit buttons.
///
/// Access: any authenticated user ([Authorize]).
/// </summary>
[Authorize]
public sealed class SummaryModel(ImportSummaryService summaryService) : PageModel
{
    /// <summary>
    /// Aggregated staging counts produced by <see cref="ImportSummaryService"/>.
    /// Null when no manifest exists.
    /// </summary>
    public ImportSummary? Summary { get; private set; }

    /// <summary>
    /// True when the page was reached from the dry-run flow (/?dryRun=true on Index).
    /// No commit buttons are shown when true.
    /// </summary>
    public bool IsDryRun { get; private set; }

    public async Task OnGetAsync(bool dryRun = false, CancellationToken ct = default)
    {
        IsDryRun = dryRun;
        Summary  = await summaryService.ComputeAsync(ct);
    }
}
