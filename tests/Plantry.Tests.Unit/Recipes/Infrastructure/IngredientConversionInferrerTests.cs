using Plantry.Recipes.Infrastructure;

namespace Plantry.Tests.Unit.Recipes.Infrastructure;

/// <summary>
/// L1 tests for <see cref="IngredientConversionInferrer"/>'s pure <c>ParseFactor</c> mapping
/// (plantry-qll2.4), exercised against recorded model output with no live API call (mirrors
/// <c>DietTagContradictionCheckerTests</c>). Covers the untrusted-input contract (ADR-007): a factor is
/// kept only when it is a finite, strictly positive number within the sanity bound — markdown fences are
/// stripped, quoted-number strings are accepted, and every malformed / null / non-positive / absurd /
/// out-of-range payload soft-fails to <c>null</c> (never throws), leaving today's unit-gap behaviour in
/// place.
/// </summary>
public sealed class IngredientConversionInferrerTests
{
    [Fact]
    public void Parses_A_Valid_Positive_Factor()
    {
        Assert.Equal(120m, IngredientConversionInferrer.ParseFactor("""{ "factor": 120 }"""));
    }

    [Fact]
    public void Parses_A_Fractional_Factor()
    {
        Assert.Equal(0.25m, IngredientConversionInferrer.ParseFactor("""{ "factor": 0.25 }"""));
    }

    [Fact]
    public void Strips_Markdown_Fences_Around_The_Json()
    {
        var raw = "```json\n{ \"factor\": 240 }\n```";
        Assert.Equal(240m, IngredientConversionInferrer.ParseFactor(raw));
    }

    [Fact]
    public void Accepts_A_Quoted_Number_String()
    {
        Assert.Equal(50m, IngredientConversionInferrer.ParseFactor("""{ "factor": "50" }"""));
    }

    [Theory]
    [InlineData("""{ "factor": null }""")]     // model declined to estimate
    [InlineData("""{ "factor": 0 }""")]        // zero is never a valid factor
    [InlineData("""{ "factor": -5 }""")]       // negative is never valid
    [InlineData("""{ "factor": "abc" }""")]    // non-numeric string
    [InlineData("""{ "weight": 120 }""")]      // missing the factor property
    [InlineData("""{ "factor": 2000000 }""")]  // above the sanity ceiling (1e6)
    [InlineData("not json at all")]             // malformed
    [InlineData("")]                            // empty
    [InlineData(null)]                          // null input
    public void Soft_Fails_To_Null_On_Unusable_Payloads(string? raw)
    {
        Assert.Null(IngredientConversionInferrer.ParseFactor(raw));
    }
}
