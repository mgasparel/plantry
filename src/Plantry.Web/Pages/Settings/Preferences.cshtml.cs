using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;

namespace Plantry.Web.Pages.Settings;

[Authorize]
public sealed partial class PreferencesModel(
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
    /// htmx fragment handler: sets a single stance and returns the updated tag-row fragment
    /// plus OOB fragments for the prefs-meta counter and the affected category count badge.
    /// Bound values: <c>tagId</c>, <c>stance</c> (Required | Preferred | Neutral | Disliked | Restricted),
    /// <c>tagName</c> (display name for re-rendering the row label).
    /// </summary>
    public async Task<IActionResult> OnPostStanceAsync(Guid tagId, string stance, string tagName = "")
    {
        CurrentUserId = await GetCurrentUserIdAsync();

        if (!IsValidStance(stance))
            return BadRequest($"Invalid stance value: {stance}");

        await service.SetStanceAsync(CurrentUserId, tagId, stance);

        // Fetch the updated view model so we can recompute the live counts.
        ViewModel = await service.GetViewModelAsync(CurrentUserId);

        // Primary swap: updated tag-row.
        var tagRowResult = Partial("Settings/_TagStanceRow",
            new TagStanceRowViewModel(tagId, tagName, stance, Saved: true));

        // OOB swap: global prefs-meta line (counter + Reset button disabled state).
        var currentMember = ViewModel.Members.FirstOrDefault(m => m.UserId == CurrentUserId);
        var metaResult = Partial("Settings/_PrefsMeta",
            new PrefsMetaViewModel(
                currentMember?.Initials ?? "Y",
                currentMember?.DisplayName ?? "You",
                ViewModel.SetCount,
                ViewModel.TotalTags));

        // OOB swap: per-category count badge for the category containing this tag.
        var affectedGroup = ViewModel.Groups.FirstOrDefault(g => g.Tags.Any(t => t.TagId == tagId));
        PartialViewResult? catCountResult = null;
        if (affectedGroup is not null)
        {
            var catSlug = CategorySlug(affectedGroup.Category);
            catCountResult = Partial("Settings/_PrefsCatCount",
                new PrefsCatCountViewModel(catSlug, affectedGroup.SetCount));
        }

        return new MultiFragmentResult(tagRowResult, metaResult, catCountResult);
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

    /// <summary>Converts a category name to a slug suitable for use in a DOM id attribute.</summary>
    /// <remarks>Single source of truth for the category slug: used both on GET (in _PreferencesGroups) and
    /// on POST (in OnPostStanceAsync) to ensure the OOB swap targets the correct element.</remarks>
    public static string CategorySlug(string category) =>
        SlugUnsafeChars().Replace(category.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugUnsafeChars();

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

/// <summary>View model for the prefs-meta OOB fragment (global counter + Reset button).</summary>
public sealed record PrefsMetaViewModel(string Initials, string DisplayName, int SetCount, int TotalTags)
{
    public int NeutralCount => TotalTags - SetCount;
}

/// <summary>View model for a per-category count badge OOB fragment.</summary>
public sealed record PrefsCatCountViewModel(string CategorySlug, int SetCount);
