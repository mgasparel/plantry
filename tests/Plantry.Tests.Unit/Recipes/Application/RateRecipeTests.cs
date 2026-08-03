using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class RateRecipeTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private static (RateRecipe Service, FakeRecipeRatingRepository Ratings, FakeRecipeRepository Recipes) Build(
        bool authenticated = true)
    {
        var ratings = new FakeRecipeRatingRepository();
        var recipes = new FakeRecipeRepository();
        var tenant = new FakeTenantContext(authenticated ? Household.Value : null);
        var service = new RateRecipe(ratings, recipes, tenant, Clock, NullLogger<RateRecipe>.Instance);
        return (service, ratings, recipes);
    }

    private static Recipe SeedRecipe(FakeRecipeRepository repo, string name = "Pasta")
    {
        var recipe = Recipe.Create(Household, name, 4, Clock).Value;
        repo.Items.Add(recipe);
        return recipe;
    }

    [Fact(DisplayName = "First rate creates a RecipeRating row")]
    public async Task First_Rate_Creates_Row()
    {
        var (service, ratings, recipes) = Build();
        var recipe = SeedRecipe(recipes);

        var result = await service.ExecuteAsync(new RateRecipeCommand(recipe.Id, UserA, 4));

        Assert.True(result.IsSuccess);
        var rating = Assert.Single(ratings.Items);
        Assert.Equal(recipe.Id, rating.RecipeId);
        Assert.Equal(UserA, rating.UserId);
        Assert.Equal(4, rating.Stars);
        Assert.Equal(1, ratings.SaveChangesCalls);
    }

    [Fact(DisplayName = "Rating again upserts the existing row rather than creating a second one")]
    public async Task Second_Rate_Upserts_Existing_Row()
    {
        var (service, ratings, recipes) = Build();
        var recipe = SeedRecipe(recipes);

        await service.ExecuteAsync(new RateRecipeCommand(recipe.Id, UserA, 3));
        var result = await service.ExecuteAsync(new RateRecipeCommand(recipe.Id, UserA, 5));

        Assert.True(result.IsSuccess);
        var rating = Assert.Single(ratings.Items);
        Assert.Equal(5, rating.Stars);
        Assert.Equal(2, ratings.SaveChangesCalls);
    }

    [Fact(DisplayName = "Different members rating the same recipe each get their own row")]
    public async Task Different_Members_Get_Separate_Rows()
    {
        var (service, ratings, recipes) = Build();
        var recipe = SeedRecipe(recipes);

        await service.ExecuteAsync(new RateRecipeCommand(recipe.Id, UserA, 3));
        await service.ExecuteAsync(new RateRecipeCommand(recipe.Id, UserB, 5));

        Assert.Equal(2, ratings.Items.Count);
        Assert.Contains(ratings.Items, r => r.UserId == UserA && r.Stars == 3);
        Assert.Contains(ratings.Items, r => r.UserId == UserB && r.Stars == 5);
    }

    [Theory(DisplayName = "Stars outside 1..5 are rejected without touching the repository")]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Rejects_Invalid_Stars(int stars)
    {
        var (service, ratings, recipes) = Build();
        var recipe = SeedRecipe(recipes);

        var result = await service.ExecuteAsync(new RateRecipeCommand(recipe.Id, UserA, stars));

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidStars", result.Error.Code);
        Assert.Empty(ratings.Items);
        Assert.Equal(0, ratings.SaveChangesCalls);
    }

    [Fact(DisplayName = "Rating a missing recipe returns NotFound")]
    public async Task Missing_Recipe_Returns_NotFound()
    {
        var (service, ratings, _) = Build();

        var result = await service.ExecuteAsync(new RateRecipeCommand(RecipeId.New(), UserA, 4));

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
        Assert.Empty(ratings.Items);
    }

    [Fact(DisplayName = "No authenticated household returns Unauthorized")]
    public async Task No_Household_Returns_Unauthorized()
    {
        var (service, _, recipes) = Build(authenticated: false);
        var recipe = SeedRecipe(recipes);

        var result = await service.ExecuteAsync(new RateRecipeCommand(recipe.Id, UserA, 4));

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
    }
}
