using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Aggregate root — one household member's 1-5 star rating of a recipe (plantry-zlwp, epic "Per-user
/// recipe ratings"). Modelled on MealPlanning's <c>UserPreference</c>: created lazily on first rate,
/// upserted after. No opinion = ABSENCE of a row (clearing deletes it) — the same convention as
/// <c>UserPreference</c>'s Neutral stance. UNIQUE (household_id, recipe_id, user_id).
/// Ratings on archived recipes persist (recipes are soft-deleted, never removed).
/// </summary>
public sealed class RecipeRating : AggregateRoot<RecipeRatingId>
{
    // Required by EF
    private RecipeRating() { }

    public HouseholdId HouseholdId { get; private set; }

    public RecipeId RecipeId { get; private set; }

    /// <summary>The rating member (soft-ref → identity user, DM-3); UNIQUE (household_id, recipe_id, user_id).</summary>
    public Guid UserId { get; private set; }

    /// <summary>Whole stars, 1-5.</summary>
    public int Stars { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Factory: creates a new rating for a (household, recipe, user). Called on first rate — subsequent
    /// rates upsert via <see cref="SetStars"/> on the loaded row (application-layer responsibility, mirroring
    /// <c>SetPreferences.SetStanceAsync</c>).
    /// </summary>
    public static RecipeRating Create(
        HouseholdId householdId, RecipeId recipeId, Guid userId, int stars, IClock clock)
    {
        ValidateStars(stars);
        return new()
        {
            Id = RecipeRatingId.New(),
            HouseholdId = householdId,
            RecipeId = recipeId,
            UserId = userId,
            Stars = stars,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
    }

    /// <summary>Updates the star value on an existing rating (the upsert's "update" branch).</summary>
    public void SetStars(int stars, IClock clock)
    {
        ValidateStars(stars);
        Stars = stars;
        UpdatedAt = clock.UtcNow;
    }

    private static void ValidateStars(int stars)
    {
        if (stars is < 1 or > 5)
            throw new ArgumentException($"Stars must be between 1 and 5 (got {stars}).", nameof(stars));
    }
}
