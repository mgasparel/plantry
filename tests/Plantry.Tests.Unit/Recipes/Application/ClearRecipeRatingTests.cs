using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class ClearRecipeRatingTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly RecipeId Recipe = RecipeId.New();
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private static (ClearRecipeRating Service, FakeRecipeRatingRepository Ratings) Build()
    {
        var ratings = new FakeRecipeRatingRepository();
        var service = new ClearRecipeRating(ratings, NullLogger<ClearRecipeRating>.Instance);
        return (service, ratings);
    }

    [Fact(DisplayName = "Clearing an existing rating removes the row — no opinion is absence of a row")]
    public async Task Clear_Removes_Existing_Row()
    {
        var (service, ratings) = Build();
        var rating = RecipeRating.Create(Household, Recipe, UserA, 4, Clock);
        ratings.Items.Add(rating);

        var result = await service.ExecuteAsync(new ClearRecipeRatingCommand(Recipe, UserA));

        Assert.True(result.IsSuccess);
        Assert.Empty(ratings.Items);
        Assert.Equal(1, ratings.SaveChangesCalls);
    }

    [Fact(DisplayName = "Clearing an already-absent rating is a no-op success")]
    public async Task Clear_Missing_Rating_Is_NoOp_Success()
    {
        var (service, ratings) = Build();

        var result = await service.ExecuteAsync(new ClearRecipeRatingCommand(Recipe, UserA));

        Assert.True(result.IsSuccess);
        Assert.Empty(ratings.Items);
        Assert.Equal(0, ratings.SaveChangesCalls);
    }

    [Fact(DisplayName = "Clearing one member's rating never touches another member's row")]
    public async Task Clear_Only_Removes_The_Acting_Members_Row()
    {
        var (service, ratings) = Build();
        var ratingA = RecipeRating.Create(Household, Recipe, UserA, 4, Clock);
        var ratingB = RecipeRating.Create(Household, Recipe, UserB, 2, Clock);
        ratings.Items.Add(ratingA);
        ratings.Items.Add(ratingB);

        var result = await service.ExecuteAsync(new ClearRecipeRatingCommand(Recipe, UserA));

        Assert.True(result.IsSuccess);
        var remaining = Assert.Single(ratings.Items);
        Assert.Equal(UserB, remaining.UserId);
    }
}
