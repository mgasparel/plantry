using Plantry.Recipes.Infrastructure;

namespace Plantry.Tests.Unit.Recipes.Infrastructure;

/// <summary>
/// L1 tests for <see cref="DietTagContradictionChecker"/>'s pure <c>MapResponse</c> mapping (plantry-qll2.3),
/// exercised against recorded model output with no live API call (mirrors <c>RecipeTagSuggesterTests</c>).
/// Covers the untrusted-input contract (ADR-007): a contradiction is kept only when BOTH its ingredient and its
/// tag match the supplied sets verbatim (case-insensitive) — the model can invent neither — markdown fences are
/// stripped, duplicates collapse, the list is capped, and every malformed/empty payload soft-fails to an empty
/// list (never throws). The checker observes only; it can never mutate a tag.
/// </summary>
public sealed class DietTagContradictionCheckerTests
{
    private static readonly IReadOnlyList<string> Ingredients = ["Parmesan", "Rigatoni", "Garlic"];
    private static readonly IReadOnlyList<string> DietTags = ["Dairy-Free", "Vegetarian"];

    [Fact]
    public void Maps_A_Valid_Contradiction_Carrying_Verbatim_Names()
    {
        var response = """
            { "contradictions": [ { "ingredient": "Parmesan", "tag": "Dairy-Free" } ] }
            """;

        var result = DietTagContradictionChecker.MapResponse(response, Ingredients, DietTags);

        var c = Assert.Single(result);
        Assert.Equal("Parmesan", c.IngredientName);
        Assert.Equal("Dairy-Free", c.DietTagName);
    }

    [Fact]
    public void Matches_Case_Insensitively_But_Returns_The_Supplied_Spelling()
    {
        var response = """
            { "contradictions": [ { "ingredient": "parMEsan", "tag": "dairy-free" } ] }
            """;

        var result = DietTagContradictionChecker.MapResponse(response, Ingredients, DietTags);

        var c = Assert.Single(result);
        Assert.Equal("Parmesan", c.IngredientName);   // household spelling wins
        Assert.Equal("Dairy-Free", c.DietTagName);
    }

    [Fact]
    public void Drops_A_Contradiction_Whose_Ingredient_Is_Not_In_The_Supplied_Set()
    {
        // The model hallucinated "Cheddar" — never offered as an ingredient, so it is dropped.
        var response = """
            { "contradictions": [ { "ingredient": "Cheddar", "tag": "Dairy-Free" } ] }
            """;

        Assert.Empty(DietTagContradictionChecker.MapResponse(response, Ingredients, DietTags));
    }

    [Fact]
    public void Drops_A_Contradiction_Whose_Tag_Is_Not_A_Recipe_Diet_Tag()
    {
        // "Vegan" is not one of this recipe's diet tags, so a clash against it is dropped.
        var response = """
            { "contradictions": [ { "ingredient": "Parmesan", "tag": "Vegan" } ] }
            """;

        Assert.Empty(DietTagContradictionChecker.MapResponse(response, Ingredients, DietTags));
    }

    [Fact]
    public void Strips_Markdown_Fences()
    {
        var response = """
            ```json
            { "contradictions": [ { "ingredient": "Parmesan", "tag": "Dairy-Free" } ] }
            ```
            """;

        Assert.Single(DietTagContradictionChecker.MapResponse(response, Ingredients, DietTags));
    }

    [Fact]
    public void Collapses_Duplicate_Ingredient_Tag_Pairs()
    {
        var response = """
            { "contradictions": [
                { "ingredient": "Parmesan", "tag": "Dairy-Free" },
                { "ingredient": "Parmesan", "tag": "Dairy-Free" }
            ] }
            """;

        Assert.Single(DietTagContradictionChecker.MapResponse(response, Ingredients, DietTags));
    }

    [Fact]
    public void Empty_Contradictions_Array_Returns_Empty()
    {
        Assert.Empty(DietTagContradictionChecker.MapResponse(
            """{ "contradictions": [] }""", Ingredients, DietTags));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ }")]
    [InlineData("""{ "contradictions": "oops" }""")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_Or_Empty_Payloads_Soft_Fail_To_Empty(string? raw)
    {
        Assert.Empty(DietTagContradictionChecker.MapResponse(raw, Ingredients, DietTags));
    }
}
