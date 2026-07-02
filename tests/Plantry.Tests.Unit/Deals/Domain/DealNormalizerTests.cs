using Plantry.Deals.Domain;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Domain;

/// <summary>
/// L1 unit tests for the pure <see cref="DealNormalizer"/> (DD4/DL-O6): determinism across
/// representative inputs, pack-size/unit/punctuation stripping, and the version stamp.
/// </summary>
public sealed class DealNormalizerTests
{
    [Fact(DisplayName = "Normalize is deterministic: same input yields the same output")]
    public void Normalize_IsDeterministic()
    {
        const string input = "Organic 2% Milk 2L";
        var first = DealNormalizer.Normalize(input);
        var second = DealNormalizer.Normalize(input);

        Assert.Equal(first, second);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact(DisplayName = "Normalize stamps the current normalizer version")]
    public void Normalize_StampsVersion()
    {
        var result = DealNormalizer.Normalize("Bananas");

        Assert.Equal(DealNormalizer.NormalizerVersion, result.NormalizerVersion);
    }

    [Theory(DisplayName = "Normalize lowercases, trims, and collapses whitespace")]
    [InlineData("  Whole  Wheat   Bread  ", "whole wheat bread")]
    [InlineData("BANANAS", "bananas")]
    [InlineData("Free-Range Eggs", "free range eggs")]
    public void Normalize_LowercasesTrimsCollapses(string input, string expected)
    {
        Assert.Equal(expected, DealNormalizer.Normalize(input).Value);
    }

    [Theory(DisplayName = "Normalize strips pack-size and unit tokens")]
    [InlineData("Milk 2L", "milk")]
    [InlineData("Chicken Breast 500g", "chicken breast")]
    [InlineData("Cola 12x355ml", "cola")]
    [InlineData("Yogurt 4pk", "yogurt")]
    [InlineData("Cheese 200 g", "cheese")]
    public void Normalize_StripsPackSizeTokens(string input, string expected)
    {
        Assert.Equal(expected, DealNormalizer.Normalize(input).Value);
    }

    [Theory(DisplayName = "Normalize strips punctuation")]
    [InlineData("Ben & Jerry's", "ben jerry s")]
    [InlineData("Coca-Cola!", "coca cola")]
    public void Normalize_StripsPunctuation(string input, string expected)
    {
        Assert.Equal(expected, DealNormalizer.Normalize(input).Value);
    }

    [Theory(DisplayName = "Normalize folds diacritics so accented and plain spellings key identically")]
    [InlineData("Crème Brûlée", "creme brulee")]
    public void Normalize_FoldsDiacritics(string input, string expected)
    {
        Assert.Equal(expected, DealNormalizer.Normalize(input).Value);
    }

    [Theory(DisplayName = "Normalize yields empty for null/blank input, still version-stamped")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankInput_YieldsEmpty(string? input)
    {
        var result = DealNormalizer.Normalize(input);

        Assert.Equal(string.Empty, result.Value);
        Assert.Equal(DealNormalizer.NormalizerVersion, result.NormalizerVersion);
    }

    [Fact(DisplayName = "Different advertised spellings of the same item normalize to one key")]
    public void Normalize_EquivalentSpellings_ShareKey()
    {
        var a = DealNormalizer.Normalize("Whole Milk 2L");
        var b = DealNormalizer.Normalize("whole milk");

        Assert.Equal(a.Value, b.Value);
    }
}
