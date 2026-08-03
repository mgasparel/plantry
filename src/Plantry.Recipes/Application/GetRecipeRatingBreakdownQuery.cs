using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Read-model query for the per-member rating breakdown popover (plantry-zlwp epic — "hover/focus any
/// pill = popover with per-member breakdown, 'You' row first"). Joins <see cref="RecipeRating"/> rows for
/// one recipe with household-member display facts (<see cref="IHouseholdMemberReader"/>, the same seam
/// the Members page uses) — only members who have actually rated appear (unrated members are absent, not
/// a zero-star row, mirroring the aggregate's own no-opinion-is-absence convention).
/// </summary>
public sealed class GetRecipeRatingBreakdownQuery(
    IRecipeRatingRepository ratings,
    IHouseholdMemberReader members)
{
    public async Task<IReadOnlyList<RecipeRatingBreakdownRow>> ExecuteAsync(
        RecipeId recipeId, Guid currentUserId, CancellationToken ct = default)
    {
        var ratingRows = await ratings.ListByRecipeAsync(recipeId, ct);
        if (ratingRows.Count == 0)
            return [];

        var memberDirectory = (await members.ListMembersAsync(ct)).ToDictionary(m => m.UserId);

        var rows = ratingRows
            .Select(r =>
            {
                memberDirectory.TryGetValue(r.UserId, out var member);
                return new RecipeRatingBreakdownRow(
                    r.UserId,
                    member?.DisplayName ?? "Household member",
                    member?.Initials ?? "?",
                    r.Stars,
                    IsCurrentUser: r.UserId == currentUserId);
            })
            // "You" row first (epic visual grammar), then by display name for a stable order.
            .OrderByDescending(r => r.IsCurrentUser)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return rows;
    }
}

/// <summary>One member's rating in the per-recipe breakdown popover.</summary>
public sealed record RecipeRatingBreakdownRow(
    Guid UserId,
    string DisplayName,
    string Initials,
    int Stars,
    bool IsCurrentUser);
