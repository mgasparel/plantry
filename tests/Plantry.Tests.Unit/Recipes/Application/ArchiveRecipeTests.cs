using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class ArchiveRecipeTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    private static (ArchiveRecipe Service, FakeRecipeRepository Repo) Build()
    {
        var repo = new FakeRecipeRepository();
        var service = new ArchiveRecipe(repo, Clock, NullLogger<ArchiveRecipe>.Instance);
        return (service, repo);
    }

    private static Recipe SeedRecipe(FakeRecipeRepository repo, string name)
    {
        var recipe = Recipe.Create(Household, name, 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], Clock);
        repo.Items.Add(recipe);
        return recipe;
    }

    [Fact]
    public async Task Archive_Unreferenced_Recipe_Succeeds()
    {
        var (service, repo) = Build();
        var recipe = SeedRecipe(repo, "Standalone");

        var result = await service.ExecuteAsync(recipe.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(recipe.ArchivedAt);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task N5_Archive_Blocked_When_Included_By_Others()
    {
        var (service, repo) = Build();
        var sub = SeedRecipe(repo, "Nacho Cheese");
        // Two parents include the sub.
        var parent1 = SeedRecipe(repo, "Nachos");
        var parent2 = SeedRecipe(repo, "Loaded Fries");
        parent1.ReplaceLines([], [new InclusionLine(sub.Id, 2m, null, 0)], Clock);
        parent2.ReplaceLines([], [new InclusionLine(sub.Id, 1m, null, 0)], Clock);

        var result = await service.ExecuteAsync(sub.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.IncludedByOthers", result.Error.Code);
        // The error message names the includer count.
        Assert.Contains("2 recipes", result.Error.Description);
        Assert.Null(sub.ArchivedAt);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task N5_Single_Includer_Message_Is_Singular()
    {
        var (service, repo) = Build();
        var sub = SeedRecipe(repo, "Pie Crust");
        var parent = SeedRecipe(repo, "Apple Pie");
        parent.ReplaceLines([], [new InclusionLine(sub.Id, 1m, null, 0)], Clock);

        var result = await service.ExecuteAsync(sub.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.IncludedByOthers", result.Error.Code);
        Assert.Contains("1 recipe", result.Error.Description);
        Assert.DoesNotContain("1 recipes", result.Error.Description);
    }

    [Fact]
    public async Task Archive_Missing_Recipe_Returns_NotFound()
    {
        var (service, _) = Build();

        var result = await service.ExecuteAsync(RecipeId.New());

        Assert.True(result.IsFailure);
    }
}
