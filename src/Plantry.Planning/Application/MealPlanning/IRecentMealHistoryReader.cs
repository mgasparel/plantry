using Plantry.Planning.Domain;
using Plantry.SharedKernel;

namespace Plantry.Planning.Application;

/// <summary>
/// Planning-owned read port for the compact retained meal-history snapshot used by generation and
/// repetition insights. The Composition adapter joins retained Planning rows to Recipes cook events;
/// Planning never reaches into either persistence model and never stores a second copy of history.
/// </summary>
public interface IRecentMealHistoryReader
{
    /// <summary>
    /// Returns retained occurrences as of <paramref name="asOfDate"/>, excluding the plan whose
    /// Monday is <paramref name="excludedWeekStart"/>. That plan's accepted meals remain a separate,
    /// stronger current-week input.
    /// </summary>
    Task<RecentMealHistorySnapshot> ReadAsync(
        HouseholdId householdId,
        DateOnly asOfDate,
        DateOnly excludedWeekStart,
        CancellationToken ct = default);
}

/// <summary>A compact, non-persisted history snapshot grouped by recipe identity.</summary>
public sealed record RecentMealHistorySnapshot(IReadOnlyList<RecentRecipeHistory> Recipes)
{
    public static RecentMealHistorySnapshot Empty { get; } = new([]);

    public RecentRecipeHistory? Find(Guid recipeId) =>
        Recipes.FirstOrDefault(recipe => recipe.RecipeId == recipeId);
}

/// <summary>
/// Recent occurrences and currently resolvable semantic facets for one recipe. Archived recipes are
/// deliberately retained here for identity/recency, while candidate discovery independently excludes
/// them so they cannot become selectable again.
/// </summary>
public sealed record RecentRecipeHistory(
    Guid RecipeId,
    string Name,
    bool IsArchived,
    IReadOnlyList<RecentMealOccurrence> Occurrences,
    IReadOnlyList<RecentRecipeFacet> Facets)
{
    /// <summary>Sum of the policy-weighted distinct real occurrences for this recipe.</summary>
    public decimal RecencyScore => Occurrences.Sum(occurrence => occurrence.NoveltyWeight);
}

/// <summary>
/// One distinct real occurrence. A linked planned dish and cook event produce one CookEvent occurrence;
/// an unlinked retained dish produces one RetainedPlan occurrence.
/// </summary>
public sealed record RecentMealOccurrence(
    DateOnly OccurredOn,
    RecentMealOccurrenceSource Source,
    decimal NoveltyWeight,
    DateTimeOffset? CookedAt = null);

public enum RecentMealOccurrenceSource
{
    RetainedPlan,
    CookEvent,
}

/// <summary>Planning-shaped semantic tag fact available for historical diversity comparison.</summary>
public sealed record RecentRecipeFacet(Guid TagId, string Name, string? Category);
