using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service for managing a member's dietary-preference profile (journey J3, §7 DM-21).
/// <para>
/// Provides the preferences page with grouped tags (via <see cref="ITagReader"/>) and household
/// members (via <see cref="IHouseholdMemberReader"/>), and handles per-tag stance mutations.
/// The <see cref="UserPreference"/> aggregate is created lazily on the first edit (M6).
/// </para>
/// </summary>
public sealed class SetPreferences(
    IUserPreferenceRepository preferences,
    ITagReader tags,
    IHouseholdMemberReader members,
    ITenantContext tenant,
    IClock clock)
{
    // ── Queries ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assembles the view model for the /Settings/Preferences page.
    /// Groups tags by cosmetic category and resolves the signed-in member's current stances.
    /// </summary>
    public async Task<PreferencesViewModel> GetViewModelAsync(
        Guid userId, CancellationToken ct = default)
    {
        var allGroups = await tags.ListGroupedAsync(ct);
        var allMembers = await members.ListMembersAsync(ct);

        // Only the signed-in user's preferences are editable; load them lazily.
        var prefs = await preferences.FindByUserIdAsync(userId, ct);

        var stanceMap = prefs?.TagStances.ToDictionary(ts => ts.TagId, ts => ts.Stance)
                        ?? new Dictionary<Guid, string>();

        var groups = allGroups.Select(g => new TagGroupViewModel(
            g.Category,
            g.CategoryHue,
            g.Tags.Select(t => new TagViewModel(
                t.TagId,
                t.Name,
                stanceMap.TryGetValue(t.TagId, out var s) ? s : "Neutral"
            )).ToList()
        )).ToList();

        var totalTags = allGroups.Sum(g => g.Tags.Count);
        var setCount = stanceMap.Count;

        return new PreferencesViewModel(
            userId,
            groups,
            allMembers,
            totalTags,
            setCount
        );
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the signed-in user's stance on <paramref name="tagId"/>. "Neutral" removes the row.
    /// Creates the <see cref="UserPreference"/> aggregate lazily if this is the first edit (M6).
    /// </summary>
    public async Task SetStanceAsync(
        Guid userId, Guid tagId, string stance, CancellationToken ct = default)
    {
        var householdId = RequireHouseholdId();
        var pref = await preferences.FindByUserIdAsync(userId, ct);

        if (pref is null)
        {
            // Lazy create — first time this member edits their profile (M6).
            if (stance == "Neutral")
                return; // Nothing to remove, nothing to create.

            pref = UserPreference.Create(householdId, userId, clock);
            pref.SetStance(tagId, stance, clock);
            await preferences.AddAsync(pref, ct);
        }
        else
        {
            pref.SetStance(tagId, stance, clock);
        }

        await preferences.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Clears all stances for the signed-in user (sets every tag to Neutral).
    /// No-op if the user has never set any preferences.
    /// </summary>
    public async Task ResetToNeutralAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await preferences.FindByUserIdAsync(userId, ct);
        if (pref is null) return;

        foreach (var ts in pref.TagStances.ToList())
            pref.ClearStance(ts.TagId, clock);

        await preferences.SaveChangesAsync(ct);
    }

    private HouseholdId RequireHouseholdId()
    {
        var id = tenant.HouseholdId
            ?? throw new InvalidOperationException("SetPreferences requires an authenticated household context.");
        return HouseholdId.From(id);
    }
}

// ── View-model records ─────────────────────────────────────────────────────────

/// <summary>View model for /Settings/Preferences — groups tags by category with current stances.</summary>
public sealed record PreferencesViewModel(
    Guid CurrentUserId,
    IReadOnlyList<TagGroupViewModel> Groups,
    IReadOnlyList<HouseholdMember> Members,
    int TotalTags,
    int SetCount)
{
    public int NeutralCount => TotalTags - SetCount;
}

public sealed record TagGroupViewModel(
    string Category,
    int? CategoryHue,
    IReadOnlyList<TagViewModel> Tags)
{
    public int SetCount => Tags.Count(t => t.Stance != "Neutral");
}

public sealed record TagViewModel(Guid TagId, string Name, string Stance);
