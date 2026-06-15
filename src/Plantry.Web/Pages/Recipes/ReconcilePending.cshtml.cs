using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Recipes.Application;

namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// On-demand entry point for <see cref="ReconcilePendingCooks"/> (plantry-292c).
/// POST /Recipes/ReconcilePending — re-drives any Pending consume lines left by interrupted cooks
/// for the current household and returns a JSON result with the reconciled line count.
/// <para>
/// This endpoint is the explicit on-demand trigger. The opportunistic trigger runs automatically
/// at <see cref="CookRecipe"/> entry (before every new cook). No background poller is wired up
/// (ADR-010 defers infra until next-cook latency proves insufficient).
/// </para>
/// </summary>
[Authorize]
public sealed class ReconcilePendingModel(ReconcilePendingCooks reconciler) : PageModel
{
    /// <summary>
    /// Runs a full reconciliation pass for the current household and returns
    /// <c>{ reconciledLineCount: N }</c> as JSON.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await reconciler.ExecuteAsync(ct);
        return new JsonResult(new { reconciledLineCount = result.ReconciledLineCount });
    }
}
