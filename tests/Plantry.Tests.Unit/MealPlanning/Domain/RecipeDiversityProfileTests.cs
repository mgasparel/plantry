using Plantry.Planning.Domain;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

public sealed class RecipeDiversityProfileTests
{
    [Fact]
    public void Create_ExactRecipeIdentity_IsConfirmed()
    {
        var profile = Profile(Guid.NewGuid(), "Soup", []);

        Assert.Equal(
            RecipeDiversityConfidence.Confirmed,
            profile.Confidence(RecipeDiversityFacet.ExactRecipe));
    }

    private static readonly Guid TofuTagId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid LegumesTagId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid FishTagId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid VeganTagId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid ThaiTagId = Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly Guid JapaneseTagId = Guid.Parse("10000000-0000-0000-0000-000000000006");
    private static readonly Guid SpicyTagId = Guid.Parse("10000000-0000-0000-0000-000000000007");

    private static readonly IReadOnlyList<RecipeSemanticTagFact> Vocabulary =
    [
        Tag(TofuTagId, "Tofu", RecipeSemanticTagCategory.Protein),
        Tag(LegumesTagId, "Legumes", RecipeSemanticTagCategory.Protein),
        Tag(FishTagId, "Fish", RecipeSemanticTagCategory.Protein),
        Tag(VeganTagId, "Vegan", RecipeSemanticTagCategory.Diet),
        Tag(ThaiTagId, "Thai", RecipeSemanticTagCategory.Cuisine),
        Tag(JapaneseTagId, "Japanese", RecipeSemanticTagCategory.Cuisine),
        Tag(SpicyTagId, "Spicy", RecipeSemanticTagCategory.Flavor),
    ];

    [Fact]
    public void Differently_Named_Recipes_Share_Confirmed_Tofu_Without_Sharing_Exact_Recipe()
    {
        var first = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            "Crispy bowls",
            [Tag(TofuTagId, "Tofu", RecipeSemanticTagCategory.Protein)]);
        var second = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            "Weeknight stir fry",
            [Tag(TofuTagId, "Tofu", RecipeSemanticTagCategory.Protein)]);

        Assert.True(first.Shares(second, RecipeDiversityFacet.Protein));
        Assert.False(first.Shares(second, RecipeDiversityFacet.ExactRecipe));
        Assert.Equal(RecipeDiversityConfidence.Confirmed, first.Confidence(RecipeDiversityFacet.Protein));
    }

    [Fact]
    public void Multiple_Proteins_And_Cuisines_Are_Retained_Without_Primary_Facet_Loss()
    {
        var profile = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000003"),
            "Mixed hot pot",
            [
                Tag(TofuTagId, "Tofu", RecipeSemanticTagCategory.Protein),
                Tag(FishTagId, "Fish", RecipeSemanticTagCategory.Protein),
                Tag(ThaiTagId, "Thai", RecipeSemanticTagCategory.Cuisine),
                Tag(JapaneseTagId, "Japanese", RecipeSemanticTagCategory.Cuisine),
                Tag(SpicyTagId, "Spicy", RecipeSemanticTagCategory.Flavor),
            ]);

        Assert.Equal([TofuTagId, FishTagId], profile.Protein.Select(v => v.TagId!.Value).Order().ToArray());
        Assert.Equal([ThaiTagId, JapaneseTagId], profile.Cuisine.Select(v => v.TagId!.Value).Order().ToArray());
        Assert.Single(profile.Flavor);
    }

    [Fact]
    public void Vegan_Tag_Does_Not_Fabricate_Protein_Or_Cuisine()
    {
        var profile = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000004"),
            "Garden supper",
            [Tag(VeganTagId, "Vegan", RecipeSemanticTagCategory.Diet)]);

        Assert.Single(profile.Diet);
        Assert.Empty(profile.Protein);
        Assert.Empty(profile.Cuisine);
        Assert.Equal(RecipeDiversityConfidence.Missing, profile.Confidence(RecipeDiversityFacet.Protein));
        Assert.Equal(RecipeDiversityConfidence.Missing, profile.Confidence(RecipeDiversityFacet.Cuisine));
    }

    [Fact]
    public void Missing_Protein_Uses_Confirmed_Catalog_Tofu_And_Legume_Facts_As_Fallback()
    {
        var untagged = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000005"),
            "Red curry",
            [],
            [
                Ingredient("30000000-0000-0000-0000-000000000001", "Extra firm tofu"),
                Ingredient("30000000-0000-0000-0000-000000000002", "Canned chickpeas"),
            ]);
        var confirmedTofu = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000006"),
            "Crispy dinner",
            [Tag(TofuTagId, "Tofu", RecipeSemanticTagCategory.Protein)]);

        Assert.Equal([TofuTagId, LegumesTagId], untagged.Protein.Select(v => v.TagId!.Value).Order().ToArray());
        Assert.All(untagged.Protein, value =>
            Assert.Equal(RecipeDiversityEvidenceSource.ConfirmedCatalogFact, value.Source));
        Assert.Equal(RecipeDiversityConfidence.Fallback, untagged.Confidence(RecipeDiversityFacet.Protein));
        Assert.True(untagged.Shares(confirmedTofu, RecipeDiversityFacet.Protein));
    }

    [Fact]
    public void Missing_Cuisine_Uses_Literal_Household_Vocabulary_Match_And_Otherwise_Remains_Missing()
    {
        var thai = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000007"),
            "Thai basil noodles",
            []);
        var unknown = Profile(
            Guid.Parse("20000000-0000-0000-0000-000000000008"),
            "Sunday noodles",
            []);

        var value = Assert.Single(thai.Cuisine);
        Assert.Equal(ThaiTagId, value.TagId);
        Assert.Equal(RecipeDiversityEvidenceSource.ConfirmedRecipeFact, value.Source);
        Assert.Equal(RecipeDiversityConfidence.Fallback, thai.Confidence(RecipeDiversityFacet.Cuisine));
        Assert.Empty(unknown.Cuisine);
    }

    private static RecipeDiversityProfile Profile(
        Guid id,
        string name,
        IReadOnlyList<RecipeSemanticTagFact> applied,
        IReadOnlyList<RecipeIngredientFact>? ingredients = null) =>
        RecipeDiversityProfile.Create(id, name, applied, Vocabulary, ingredients ?? []);

    private static RecipeSemanticTagFact Tag(
        Guid id,
        string name,
        RecipeSemanticTagCategory category) => new(id, name, category);

    private static RecipeIngredientFact Ingredient(string id, string name) => new(Guid.Parse(id), name);
}
