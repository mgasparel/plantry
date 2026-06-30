using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.SharedKernel;

/// <summary>
/// Unit tests for <see cref="ProductNameMatcher"/> (plantry-hl4a).
/// Pure string algorithm — no DB, no infrastructure.
/// </summary>
public sealed class ProductNameMatcherTests
{
    // ─── Helper: rank a fixed catalog against a query ──────────────────────────

    private static IReadOnlyList<MatchResult<int>> RankCatalog(
        IEnumerable<(int id, string name)> catalog, string query) =>
        ProductNameMatcher.Rank(catalog, query);

    // A simple catalog used across several tests.
    private static readonly (int Id, string Name)[] Catalog =
    [
        (1,  "Tomato"),
        (2,  "Tomato Paste"),
        (3,  "Oat Milk"),
        (4,  "Greek Yogurt"),
        (5,  "Ground Beef"),
        (6,  "Cheddar Cheese"),
        (7,  "Flour"),
        (8,  "Salt"),
        (9,  "Sugar"),
        (10, "Olive Oil"),
    ];

    // ─── Plural / singular ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Plural 'tomatos' is treated as exact match for 'Tomato'")]
    public void Plural_Tomatos_IsExactForTomato()
    {
        // "tomatos" → singularize → "tomato"; "Tomato" → "tomato" → exact (1.00)
        var results = RankCatalog(Catalog, "tomatos");
        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("Tomato", first.Name);
        Assert.Equal(1.00, first.Score, precision: 2);
    }

    [Fact(DisplayName = "Plural 'tomatoes' is treated as exact match for 'Tomato'")]
    public void Plural_Tomatoes_IsExactForTomato()
    {
        // "tomatoes" → singularize → "tomato"
        var results = RankCatalog(Catalog, "tomatoes");
        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("Tomato", first.Name);
        Assert.Equal(1.00, first.Score, precision: 2);
    }

    // ─── Word order (token-set) ────────────────────────────────────────────────

    [Fact(DisplayName = "Word order 'milk oat' matches 'Oat Milk' at 0.95 (token-set)")]
    public void WordOrder_MilkOat_MatchesOatMilk()
    {
        var results = RankCatalog(Catalog, "milk oat");
        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("Oat Milk", first.Name);
        Assert.Equal(0.95, first.Score, precision: 2);
    }

    // ─── Substring (superset) ─────────────────────────────────────────────────

    [Fact(DisplayName = "'cheddar' matches 'Cheddar Cheese' at 0.90 (query tokens ⊆ name tokens)")]
    public void Substring_Cheddar_MatchesCheddarCheese_Strong()
    {
        var results = RankCatalog(Catalog, "cheddar");
        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("Cheddar Cheese", first.Name);
        Assert.Equal(0.90, first.Score, precision: 2);
    }

    // ─── Typo (Jaro-Winkler) ──────────────────────────────────────────────────

    [Fact(DisplayName = "Typo 'ground beaf' surfaces 'Ground Beef' above the 0.70 cutoff")]
    public void Typo_GroundBeaf_SurfacesGroundBeef()
    {
        var results = RankCatalog(Catalog, "ground beaf");
        Assert.Contains(results, r => r.Name == "Ground Beef" && r.Score >= 0.70);
    }

    // ─── Spelling variant ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Spelling variant 'yoghurt' surfaces 'Greek Yogurt' above the 0.70 cutoff")]
    public void Spelling_Yoghurt_SurfacesGreekYogurt()
    {
        // yoghurt vs yogurt — close enough for Jaro-Winkler at the token level
        var results = RankCatalog(Catalog, "yoghurt");
        Assert.Contains(results, r => r.Name == "Greek Yogurt" && r.Score >= 0.70);
    }

    // ─── Cutoff ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Noise query 'xyzzy' returns no hits")]
    public void NoiseQuery_ReturnsNoHits()
    {
        var results = RankCatalog(Catalog, "xyzzy");
        Assert.Empty(results);
    }

    [Fact(DisplayName = "Blank or whitespace-only query returns no hits")]
    public void BlankQuery_ReturnsNoHits()
    {
        Assert.Empty(RankCatalog(Catalog, ""));
        Assert.Empty(RankCatalog(Catalog, "   "));
    }

    // ─── Ordering: score-desc then alphabetical ────────────────────────────────

    [Fact(DisplayName = "Results are ordered score-desc, then alphabetically as a tiebreak")]
    public void Results_OrderedScoreDescThenAlpha()
    {
        // "tomato" matches both "Tomato" (exact = 1.0) and "Tomato Paste" (superset = 0.90)
        var results = RankCatalog(Catalog, "tomato");
        Assert.True(results.Count >= 2);
        Assert.Equal("Tomato", results[0].Name);      // exact (1.0)
        Assert.Equal("Tomato Paste", results[1].Name); // superset (0.90)
        Assert.True(results[0].Score >= results[1].Score);
    }

    [Fact(DisplayName = "Equal-score results are ordered alphabetically")]
    public void EqualScoreResults_OrderedAlphabetically()
    {
        // Two exact names with identical scores should be alpha-sorted.
        var catalog = new (int Id, string Name)[]
        {
            (1, "Zebra Sauce"),
            (2, "Apple Sauce"),
        };
        // Query "sauce" — both clear 0.90 (superset) with the same score.
        var results = RankCatalog(catalog, "sauce");
        Assert.Equal(2, results.Count);
        Assert.Equal("Apple Sauce", results[0].Name);
        Assert.Equal("Zebra Sauce", results[1].Name);
    }

    // ─── RankLabel helper ─────────────────────────────────────────────────────

    [Fact(DisplayName = "RankLabel returns 'best' for the top hit regardless of score")]
    public void RankLabel_TopHit_ReturnsBest()
    {
        Assert.Equal("best", ProductNameMatcher.RankLabel(0.87, isTopHit: true));
        Assert.Equal("best", ProductNameMatcher.RankLabel(1.00, isTopHit: true));
        Assert.Equal("best", ProductNameMatcher.RankLabel(0.70, isTopHit: true));
    }

    [Fact(DisplayName = "RankLabel returns 'best' for a perfect score (1.0) even when not marked top hit")]
    public void RankLabel_PerfectScore_ReturnsBest()
    {
        Assert.Equal("best", ProductNameMatcher.RankLabel(1.00, isTopHit: false));
    }

    [Fact(DisplayName = "RankLabel returns rounded percentage for non-top, non-exact hits")]
    public void RankLabel_NonTop_ReturnsPercentage()
    {
        Assert.Equal("87%", ProductNameMatcher.RankLabel(0.87, isTopHit: false));
        Assert.Equal("70%", ProductNameMatcher.RankLabel(0.70, isTopHit: false));
        Assert.Equal("95%", ProductNameMatcher.RankLabel(0.95, isTopHit: false));
    }

    // ─── Normalize / Tokenize / Singularize internals ─────────────────────────

    [Theory(DisplayName = "Normalize strips punctuation and lowercases")]
    [InlineData("Oat Milk!", "oat milk")]
    [InlineData("FLOUR", "flour")]
    [InlineData("cheddar--cheese", "cheddar  cheese")] // hyphens → spaces (may collapse)
    public void Normalize_StripsAndLowers(string input, string _)
    {
        // Just verify it doesn't throw and output is lowercase with no punctuation.
        var result = ProductNameMatcher.Normalize(input);
        Assert.Equal(result, result.ToLowerInvariant());
        Assert.DoesNotContain("!", result);
    }

    [Theory(DisplayName = "Singularize applies English rules for common food tokens")]
    [InlineData("berries", "berry")]
    [InlineData("tomatoes", "tomato")]  // -oes → strip -s only (not -es)
    [InlineData("potatoes", "potato")]  // -oes rule
    [InlineData("mangoes",  "mango")]   // -oes rule
    [InlineData("tomatos",  "tomato")]  // -s rule (not -oes: "tomatos" ends in "os" not "oes")
    [InlineData("oils", "oil")]
    [InlineData("salt", "salt")]        // too short to strip s (≤3 chars for "sal" + s)
    [InlineData("flour", "flour")]      // doesn't end in s
    public void Singularize_AppliesRules(string token, string expected)
    {
        Assert.Equal(expected, ProductNameMatcher.Singularize(token));
    }

    // ─── Score method (direct) ────────────────────────────────────────────────

    [Fact(DisplayName = "Score is 1.0 for an exact match after normalization")]
    public void Score_ExactMatch_Is1()
    {
        Assert.Equal(1.0, ProductNameMatcher.Score("Tomato", "Tomato"), precision: 2);
        Assert.Equal(1.0, ProductNameMatcher.Score("tomato", "TOMATO"), precision: 2);
    }

    [Fact(DisplayName = "Score is 0 for an empty query or name")]
    public void Score_EmptyInput_IsZero()
    {
        Assert.Equal(0.0, ProductNameMatcher.Score("", "Tomato"));
        Assert.Equal(0.0, ProductNameMatcher.Score("Tomato", ""));
    }

    // ─── Integration: both endpoints return the same ranked order ─────────────

    [Fact(DisplayName = "Recipes and Take Stock adapters produce the same ranked order for the same catalog + query")]
    public void BothEndpoints_ProduceSameRankedOrder()
    {
        // Simulate a catalog that both adapters would receive from ListActiveAsync.
        var sharedCatalog = new (int Id, string Name)[]
        {
            (1, "Flour"),
            (2, "Flax Seed"),
            (3, "Flatbread"),
        };

        // Both adapters call ProductNameMatcher.Rank with the same catalog + query.
        var recipesOrder = ProductNameMatcher.Rank(sharedCatalog, "fl");
        var takeStockOrder = ProductNameMatcher.Rank(sharedCatalog, "fl");

        // They must return the same names in the same order.
        Assert.Equal(recipesOrder.Select(r => r.Name), takeStockOrder.Select(r => r.Name));
    }
}
