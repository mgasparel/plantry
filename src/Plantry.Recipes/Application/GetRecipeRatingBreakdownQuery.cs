using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Read-model query for the per-member rating breakdown popover (plantry-zlwp epic — "hover/focus any
/// pill = popover with per-member breakdown, 'You' row first"). Joins <see cref="RecipeRating"/> rows for
/// one recipe with household-member display facts (<see cref="IHouseholdMemberReader"/>, the same seam
/// the Members page uses).
///
/// <para>
/// The row set is the UNION of every current household member and every user who has rated this recipe
/// (plantry-zlwp.3) — NOT rated-members-only. The Details page popover renders "not rated" for a member
/// who hasn't rated (<c>.rating-pop-row__unrated</c>, the visual grammar's own decision, see
/// <c>.preview/recipe-rating-display-riffs.html</c> section H), so the read model must surface those rows
/// too, with <see cref="RecipeRatingBreakdownRow.Stars"/> null. This is purely a READ-time composition —
/// the aggregate's own no-opinion-is-absence persistence convention (no <see cref="RecipeRating"/> row for
/// an unrated member) is unchanged; the union only happens here, at render time. A user who rated but is
/// no longer resolvable in the member directory (left the household, or the directory lookup missed) still
/// gets a row via the ratings side of the union, falling back to a generic name/initials.
/// </para>
/// </summary>
public sealed class GetRecipeRatingBreakdownQuery(
    IRecipeRatingRepository ratings,
    IHouseholdMemberReader members)
{
    public async Task<IReadOnlyList<RecipeRatingBreakdownRow>> ExecuteAsync(
        RecipeId recipeId, Guid currentUserId, CancellationToken ct = default)
    {
        var ratingRows = await ratings.ListByRecipeAsync(recipeId, ct);
        var starsByUser = ratingRows.ToDictionary(r => r.UserId, r => r.Stars);

        var memberDirectory = await members.ListMembersAsync(ct);
        var memberById = memberDirectory.ToDictionary(m => m.UserId);

        // Union: every directory member ∪ every rater not (any longer) in the directory.
        var participantIds = memberById.Keys.Union(starsByUser.Keys).ToList();
        if (participantIds.Count == 0)
            return [];

        var rows = participantIds
            .Select(userId =>
            {
                memberById.TryGetValue(userId, out var member);
                int? stars = starsByUser.TryGetValue(userId, out var s) ? s : null;
                return new RecipeRatingBreakdownRow(
                    userId,
                    member?.DisplayName ?? "Household member",
                    member?.Initials ?? "?",
                    stars,
                    IsCurrentUser: userId == currentUserId);
            })
            // "You" row first (epic visual grammar), then by display name for a stable order.
            .OrderByDescending(r => r.IsCurrentUser)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return rows;
    }
}

/// <summary>
/// One member's rating in the per-recipe breakdown popover. <see cref="Stars"/> is null when this
/// member is a current household member who has not rated the recipe (rendered as "not rated" —
/// distinguishing "no opinion" from a literal zero-star rating, which the domain never allows).
/// </summary>
public sealed record RecipeRatingBreakdownRow(
    Guid UserId,
    string DisplayName,
    string Initials,
    int? Stars,
    bool IsCurrentUser);
