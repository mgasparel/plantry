using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class RecipeDiversityMetadataQueryTests
{
    private static readonly HouseholdId Household = HouseholdId.From(
        Guid.Parse("40000000-0000-0000-0000-000000000001"));
    private static readonly FixedClock Clock = new(
        new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Reports_Only_Missing_Protein_And_Cuisine_And_Retains_Multiple_Values()
    {
        var recipes = new FakeRecipeRepository();
        var tags = new FakeTagRepository();
        var tofu = AddTag(tags, "Tofu", TagCategory.Protein);
        var legumes = AddTag(tags, "Legumes", TagCategory.Protein);
        var thai = AddTag(tags, "Thai", TagCategory.Cuisine);
        var japanese = AddTag(tags, "Japanese", TagCategory.Cuisine);
        var vegan = AddTag(tags, "Vegan", TagCategory.Diet);

        AddRecipe(recipes, "Complete fusion", tofu.Id, legumes.Id, thai.Id, japanese.Id);
        AddRecipe(recipes, "Missing cuisine", tofu.Id);
        AddRecipe(recipes, "Untagged");
        AddRecipe(recipes, "Vegan only", vegan.Id);

        var result = await new RecipeDiversityMetadataQuery(recipes, tags).ExecuteAsync();

        Assert.Equal(["Missing cuisine", "Untagged", "Vegan only"], result.Select(g => g.Name).ToArray());
        Assert.Equal([TagCategory.Cuisine], result[0].MissingCategories);
        Assert.Equal([TagCategory.Protein, TagCategory.Cuisine], result[1].MissingCategories);
        Assert.Equal([TagCategory.Protein, TagCategory.Cuisine], result[2].MissingCategories);
        Assert.DoesNotContain(result, gap => gap.Name == "Complete fusion");
    }

    [Fact]
    public async Task Archived_Applied_Tag_Remains_Authoritative_For_Coverage()
    {
        var recipes = new FakeRecipeRepository();
        var tags = new FakeTagRepository();
        var tofu = AddTag(tags, "Tofu", TagCategory.Protein);
        var thai = AddTag(tags, "Thai", TagCategory.Cuisine);
        thai.Archive(Clock);
        AddRecipe(recipes, "Archived cuisine vocabulary", tofu.Id, thai.Id);

        var result = await new RecipeDiversityMetadataQuery(recipes, tags).ExecuteAsync();

        Assert.Empty(result);
    }

    private static Tag AddTag(FakeTagRepository repository, string name, TagCategory category)
    {
        var tag = Tag.Create(Household, name, category, Clock);
        repository.Items.Add(tag);
        return tag;
    }

    private static void AddRecipe(FakeRecipeRepository repository, string name, params TagId[] tagIds)
    {
        var recipe = Recipe.Create(Household, name, 4, Clock).Value;
        recipe.SetTags(tagIds, Clock);
        repository.Items.Add(recipe);
    }
}
