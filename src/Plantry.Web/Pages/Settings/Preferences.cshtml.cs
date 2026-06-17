using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;

namespace Plantry.Web.Pages.Settings;

[Authorize]
public sealed class PreferencesModel(
    SetPreferences service,
    UserManager<AppUser> userManager) : PageModel
{
    public PreferencesViewModel? ViewModel { get; private set; }

    /// <summary>The signed-in user's id — used to scope edits to the current member only.</summary>
    public Guid CurrentUserId { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        CurrentUserId = await GetCurrentUserIdAsync();
        ViewModel = await service.GetViewModelAsync(CurrentUserId);
        return Page();
    }

    /// <summary>
    /// htmx fragment handler: sets a single stance and returns the updated tag-row fragment.
    /// Bound values: <c>tagId</c>, <c>stance</c> (Required | Preferred | Neutral | Disliked | Restricted),
    /// <c>tagName</c> (display name for re-rendering the row label).
    /// </summary>
    public async Task<IActionResult> OnPostStanceAsync(Guid tagId, string stance, string tagName = "")
    {
        CurrentUserId = await GetCurrentUserIdAsync();

        if (!IsValidStance(stance))
            return BadRequest($"Invalid stance value: {stance}");

        await service.SetStanceAsync(CurrentUserId, tagId, stance);

        // Return the updated tag-row fragment for htmx to swap.
        return Partial("Settings/_TagStanceRow", new TagStanceRowViewModel(tagId, tagName, stance, Saved: true));
    }

    /// <summary>htmx fragment handler: resets all stances to Neutral.</summary>
    public async Task<IActionResult> OnPostResetAsync()
    {
        CurrentUserId = await GetCurrentUserIdAsync();
        await service.ResetToNeutralAsync(CurrentUserId);

        // Return the full preferences fragment with cleared stances.
        ViewModel = await service.GetViewModelAsync(CurrentUserId);
        return Partial("_PreferencesGroups", ViewModel);
    }

    private async Task<Guid> GetCurrentUserIdAsync()
    {
        var user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("No authenticated user.");
        return Guid.Parse(user.Id);
    }

    private static bool IsValidStance(string stance) =>
        stance is "Required" or "Preferred" or "Neutral" or "Disliked" or "Restricted";
}

/// <summary>View model for a single tag-row partial (the htmx swap target).</summary>
public sealed record TagStanceRowViewModel(Guid TagId, string TagName, string Stance, bool Saved);
