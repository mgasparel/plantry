using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class GetRecipeRatingBreakdownQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly RecipeId Recipe = RecipeId.New();
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private static (GetRecipeRatingBreakdownQuery Query, FakeRecipeRatingRepository Ratings, FakeHouseholdMemberReader Members) Build()
    {
        var ratings = new FakeRecipeRatingRepository();
        var members = new FakeHouseholdMemberReader();
        var query = new GetRecipeRatingBreakdownQuery(ratings, members);
        return (query, ratings, members);
    }

    [Fact(DisplayName = "No ratings returns an empty breakdown")]
    public async Task No_Ratings_Returns_Empty()
    {
        var (query, _, _) = Build();

        var rows = await query.ExecuteAsync(Recipe, UserA);

        Assert.Empty(rows);
    }

    [Fact(DisplayName = "The current user's row is first, followed by other members sorted by display name")]
    public async Task You_Row_First_Then_By_Display_Name()
    {
        var (query, ratings, members) = Build();
        ratings.Items.Add(RecipeRating.Create(Household, Recipe, UserA, 4, Clock));
        ratings.Items.Add(RecipeRating.Create(Household, Recipe, UserB, 5, Clock));
        members.Items.Add(new HouseholdMember(UserA, "Zara", "Z"));
        members.Items.Add(new HouseholdMember(UserB, "Amir", "A"));

        var rows = await query.ExecuteAsync(Recipe, currentUserId: UserA);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsCurrentUser);
        Assert.Equal(UserA, rows[0].UserId);
        Assert.Equal("Zara", rows[0].DisplayName);
        Assert.Equal(4, rows[0].Stars);
        Assert.False(rows[1].IsCurrentUser);
        Assert.Equal(UserB, rows[1].UserId);
    }

    [Fact(DisplayName = "A rating from a member absent from the directory falls back to a generic display name")]
    public async Task Unknown_Member_Falls_Back_To_Generic_Name()
    {
        var (query, ratings, _) = Build();
        ratings.Items.Add(RecipeRating.Create(Household, Recipe, UserA, 3, Clock));
        // No matching entry registered in `members` — directory lookup misses.

        var rows = await query.ExecuteAsync(Recipe, currentUserId: UserB);

        var row = Assert.Single(rows);
        Assert.Equal("Household member", row.DisplayName);
        Assert.Equal("?", row.Initials);
    }

    [Fact(DisplayName = "Only recipes with ratings for THIS recipe id appear — another recipe's ratings are excluded")]
    public async Task Only_This_Recipes_Ratings_Appear()
    {
        var (query, ratings, _) = Build();
        var otherRecipe = RecipeId.New();
        ratings.Items.Add(RecipeRating.Create(Household, Recipe, UserA, 4, Clock));
        ratings.Items.Add(RecipeRating.Create(Household, otherRecipe, UserB, 5, Clock));

        var rows = await query.ExecuteAsync(Recipe, currentUserId: UserA);

        var row = Assert.Single(rows);
        Assert.Equal(UserA, row.UserId);
    }
}
