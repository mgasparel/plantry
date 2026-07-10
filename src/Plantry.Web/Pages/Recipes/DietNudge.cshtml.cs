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
///   Gate 5 / C9); swaps the callout away AND returns a one-element <c>hx-swap-oob="delete"</c> fragment that
///   removes the just-dropped tag's hero chip on the Details page in place (plantry-klvd), so the header no longer
///   diverges from server state until the next reload.</item>
/// </list>
/// The page renders as a bare fragment (<c>Layout = null</c> in the view); <b>Dismiss</b> returns an empty body so
/// htmx removes the callout in place, and <b>RemoveTag</b> returns only the out-of-band chip-delete element — its
/// empty primary body removes the callout exactly the same way.</para>
/// </summary>
[Authorize]
public sealed class DietNudgeModel(DietTagNudgeService nudge) : PageModel
{
    /// <summary>The resolved nudge to render, or null when there is nothing to show (gate off, no change, no clash).</summary>
    public DietTagNudgeView? Nudge { get; private set; }

    /// <summary>
    /// The resolved reverse-ripple nudge to render (recipe-composition.md D10), or null when there is nothing to show.
    /// Set only by <see cref="OnGetRippleAsync"/>; names the including PARENT whose expanded set now contradicts its
    /// Diet tag, surfaced on the saved SUB's landing.
    /// </summary>
    public DietTagRippleNudgeView? Ripple { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Nudge = await nudge.EvaluateAsync(RecipeId.From(id), ct);
        return Page();
    }

    /// <summary>
    /// Reverse-ripple deferred check (recipe-composition.md D10): loaded by an htmx placeholder on a saved SUB's
    /// landing, one per candidate parent the editor's cheap guard flagged. <paramref name="id"/> is the including
    /// PARENT's id; runs the same gate + untrusted LLM check as <see cref="OnGetAsync"/> and renders the parent-named
    /// one-line <c>.callout</c> (or nothing when the gate is off / the set is reconciled / nothing contradicts).
    /// </summary>
    public async Task<IActionResult> OnGetRippleAsync(Guid id, CancellationToken ct)
    {
        Ripple = await nudge.EvaluateRippleAsync(RecipeId.From(id), ct);
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
        // Surgical live update (plantry-klvd): the primary swap body is empty, so htmx removes the #diet-nudge
        // callout in place as before; the lone out-of-band element rides alongside to delete just the removed
        // tag's hero chip (id set in Details.cshtml) so the Details header no longer diverges from server state.
        // hx-swap-oob="delete" drops the matched element regardless of this element's contents. tagId is a Guid,
        // so the interpolated id is a safe, fixed-shape token (no user text) — no HTML-encoding concern.
        return Content(
            $"<span id=\"recipe-tag-{tagId}\" hx-swap-oob=\"delete\"></span>",
            "text/html");
    }

    /// <summary>
    /// Reverse-ripple "Remove &lt;tag&gt; tag" (recipe-composition.md D10): the user drops the contradicted Diet tag
    /// from the including PARENT (<paramref name="id"/>) themselves — the AI never mutates tags (Gate 5 / C9) — which
    /// also records the parent's expanded set as reconciled so it does not re-nag. Unlike the direct
    /// <see cref="OnPostRemoveTagAsync"/>, this returns an empty body only: the landing is the SUB's page, where the
    /// parent's hero chip is not rendered, so there is nothing to live-remove out-of-band. The empty body swaps the
    /// ripple callout away in place ("Keep it" reuses <see cref="OnPostDismissAsync"/>, which stamps the same hash).
    /// </summary>
    public async Task<IActionResult> OnPostRippleRemoveTagAsync(Guid id, Guid tagId, CancellationToken ct)
    {
        await nudge.RemoveTagAsync(RecipeId.From(id), tagId, ct);
        return Content("", "text/html");
    }
}
