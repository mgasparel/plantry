using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;

namespace Plantry.Tests.Unit.Recipes.Infrastructure;

/// <summary>
/// L1 tests for <see cref="RecipeTagSuggester"/>'s pure <c>MapResponse</c> mapping — exercised against
/// recorded model output with no live API call (mirrors <c>DealMatcherTests</c>). Covers the
/// untrusted-input contract (ADR-007): a returned name matching the household vocabulary resolves to that
/// tag's id + category (an existing pick), any other name becomes a NEW-tag proposal, markdown fences are
/// stripped, duplicates/blank names are dropped, the list is capped, and every malformed/empty payload
/// soft-fails to an empty list (never throws). The suggester surface returns proposals only — it can never
/// apply or mint a tag.
/// </summary>
public sealed class RecipeTagSuggesterTests
{
    private static readonly Guid ChickenTagId = Guid.Parse("0193b4a0-2222-7000-8000-000000000001");
    private static readonly Guid QuickTagId = Guid.Parse("0193b4a0-2222-7000-8000-000000000002");

    private static readonly IReadOnlyList<TagVocabularyEntry> Vocabulary =
    [
        new(ChickenTagId, "Chicken", TagCategory.Protein),
        new(QuickTagId, "Quick", null),
    ];

    [Fact]
    public void Resolves_A_Vocabulary_Match_To_Its_Existing_Tag_Id_And_Category()
    {
        var response = """
            { "tags": [ { "name": "Chicken", "category": "Diet" } ] }
            """;

        var result = RecipeTagSuggester.MapResponse(response, Vocabulary);

        var s = Assert.Single(result);
        Assert.Equal("Chicken", s.Name);
        Assert.Equal(ChickenTagId, s.ExistingTagId);
        Assert.False(s.IsNew);
        // The household's own category wins over the model's — Protein, not the model's "Diet".
        Assert.Equal(TagCategory.Protein, s.Category);
    }

    [Fact]
    public void Matches_Vocabulary_Case_Insensitively()
    {
        var response = """{ "tags": [ { "name": "chicken" } ] }""";

        var s = Assert.Single(RecipeTagSuggester.MapResponse(response, Vocabulary));

        Assert.Equal(ChickenTagId, s.ExistingTagId);
        Assert.False(s.IsNew);
    }

    [Fact]
    public void Treats_An_Unknown_Name_As_A_New_Tag_With_The_Parsed_Category()
    {
        var response = """{ "tags": [ { "name": "Creamy", "category": "Flavor" } ] }""";

        var s = Assert.Single(RecipeTagSuggester.MapResponse(response, Vocabulary));

        Assert.Equal("Creamy", s.Name);
        Assert.Null(s.ExistingTagId);
        Assert.True(s.IsNew);
        Assert.Equal(TagCategory.Flavor, s.Category);
    }

    [Fact]
    public void New_Tag_With_Unknown_Or_Null_Category_Gets_A_Null_Category()
    {
        var response = """{ "tags": [ { "name": "Weeknight", "category": "banana" }, { "name": "Cozy", "category": null } ] }""";

        var result = RecipeTagSuggester.MapResponse(response, Vocabulary);

        Assert.Equal(2, result.Count);
        Assert.All(result, s => Assert.True(s.IsNew));
        Assert.All(result, s => Assert.Null(s.Category));
    }

    [Fact]
    public void Strips_A_Markdown_Json_Fence()
    {
        var response = "```json\n{ \"tags\": [ { \"name\": \"Quick\" } ] }\n```";

        var s = Assert.Single(RecipeTagSuggester.MapResponse(response, Vocabulary));

        Assert.Equal(QuickTagId, s.ExistingTagId);
    }

    [Fact]
    public void Collapses_Duplicate_Names_To_The_First()
    {
        var response = """{ "tags": [ { "name": "Chicken" }, { "name": "chicken" }, { "name": "Chicken" } ] }""";

        Assert.Single(RecipeTagSuggester.MapResponse(response, Vocabulary));
    }

    [Fact]
    public void Drops_Blank_Names()
    {
        var response = """{ "tags": [ { "name": "  " }, { "name": "" }, { "name": "Quick" } ] }""";

        var s = Assert.Single(RecipeTagSuggester.MapResponse(response, Vocabulary));
        Assert.Equal("Quick", s.Name);
    }

    [Fact]
    public void Caps_The_Result_At_Six_Suggestions()
    {
        var names = Enumerable.Range(1, 10).Select(i => $$"""{ "name": "Tag{{i}}" }""");
        var response = $$"""{ "tags": [ {{string.Join(",", names)}} ] }""";

        Assert.Equal(6, RecipeTagSuggester.MapResponse(response, Vocabulary).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"tags\": \"not an array\" }")]
    [InlineData("{ \"nope\": [] }")]
    [InlineData("[]")]
    public void Soft_Fails_To_Empty_On_Empty_Or_Malformed_Content(string? raw)
    {
        Assert.Empty(RecipeTagSuggester.MapResponse(raw, Vocabulary));
    }

    [Fact]
    public void Empty_Tags_Array_Maps_To_Empty_List()
    {
        Assert.Empty(RecipeTagSuggester.MapResponse("""{ "tags": [] }""", Vocabulary));
    }

    [Fact]
    public void Maps_A_Mixed_Existing_And_New_Batch()
    {
        // The acceptance scenario: chicken/cream recipe surfaces a protein match + a new diet-adjacent tag.
        var response = """
            {
              "tags": [
                { "name": "Chicken", "category": "Protein" },
                { "name": "Creamy", "category": "Flavor" }
              ]
            }
            """;

        var result = RecipeTagSuggester.MapResponse(response, Vocabulary);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => !s.IsNew && s.ExistingTagId == ChickenTagId);
        Assert.Contains(result, s => s.IsNew && s.Name == "Creamy");
    }
}
