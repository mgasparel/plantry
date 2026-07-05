using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;

namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// Fragment-only page backing the edit-moment diet-tag contradiction nudge (plantry-qll2.3). It is loaded by a
/// deferred htmx swap on the post-save recipe Details landing (never on a plain view), so the household
/// assistive-AI gate + the untrusted LLM call live off the save's critical path — save latency is untouched (C9,
/// the save is never blocked).
///
/// <para>All read/gate/LLM orchestration is in <see cref="DietTagNudgeService"/>; this page is a thin htmx seam:
/// <list type="bullet">
///   <item><b>GET</b> — runs the deferred check and renders the one-line <c>.callout</c> (or nothing).</item>
///   <item><b>POST Dismiss</b> ("Keep it") — records the current ingredient set as reconciled; swaps the callout away.</item>
///   <item><b>POST RemoveTag</b> — the user drops the contradicted diet tag themselves (the AI never mutates tags,
///   Gate 5 / C9); swaps the callout away.</item>
/// </list>
/// The page renders as a bare fragment (<c>Layout = null</c> in the view); the POST handlers return an empty body
/// so htmx removes the callout in place.</para>
/// </summary>
[Authorize]
public sealed class DietNudgeModel(DietTagNudgeService nudge) : PageModel
{
    /// <summary>The resolved nudge to render, or null when there is nothing to show (gate off, no change, no clash).</summary>
    public DietTagNudgeView? Nudge { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Nudge = await nudge.EvaluateAsync(RecipeId.From(id), ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDismissAsync(Guid id, CancellationToken ct)
    {
        await nudge.DismissAsync(RecipeId.From(id), ct);
        return Content("", "text/html");
    }

    public async Task<IActionResult> OnPostRemoveTagAsync(Guid id, Guid tagId, CancellationToken ct)
    {
        await nudge.RemoveTagAsync(RecipeId.From(id), tagId, ct);
        return Content("", "text/html");
    }
}
