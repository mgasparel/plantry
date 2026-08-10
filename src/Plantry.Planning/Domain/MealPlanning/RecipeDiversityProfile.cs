namespace Plantry.Planning.Domain;

/// <summary>
/// Planning-owned semantic categories for recipe tags. This mirrors the facts exposed by the Recipes
/// anti-corruption read port without introducing a dependency on the Recipes bounded context.
/// </summary>
public enum RecipeSemanticTagCategory
{
    Diet,
    Protein,
    Flavor,
    Cuisine,
}

/// <summary>
/// One user-maintained recipe tag fact crossing the Recipes→Planning read boundary. The stable id remains
/// the hard-constraint identity; display name and category are additive semantic facts for selection.
/// </summary>
public sealed record RecipeSemanticTagFact(
    Guid TagId,
    string DisplayName,
    RecipeSemanticTagCategory? Category);

/// <summary>A confirmed Catalog ingredient fact used only to build a compact diversity fallback.</summary>
public sealed record RecipeIngredientFact(Guid ProductId, string DisplayName);

/// <summary>The comparable dimensions represented by <see cref="RecipeDiversityProfile"/>.</summary>
public enum RecipeDiversityFacet
{
    ExactRecipe,
    Diet,
    Protein,
    Cuisine,
    Flavor,
}

/// <summary>How a semantic value entered a diversity profile.</summary>
public enum RecipeDiversityEvidenceSource
{
    /// <summary>The household explicitly applied the categorized tag to the recipe.</summary>
    ConfirmedTag,

    /// <summary>A confirmed Catalog product name matched the household's categorized vocabulary.</summary>
    ConfirmedCatalogFact,

    /// <summary>The user-authored recipe name matched the household's categorized vocabulary.</summary>
    ConfirmedRecipeFact,
}

/// <summary>Confidence state for one diversity facet.</summary>
public enum RecipeDiversityConfidence
{
    Missing,
    Fallback,
    Confirmed,
}

/// <summary>
/// One deterministic comparable value. <paramref name="Key"/> is stable within a household: recipe identity
/// for the exact facet and tag identity for semantic facets. Fallback values deliberately reuse the matched
/// vocabulary tag's key, allowing a confirmed "Tofu" tag and a catalog-backed tofu fallback to compare while
/// still carrying different evidence sources. A fallback never enters <c>CandidateRecipe.TagIds</c> and can
/// therefore never satisfy a hard tag constraint.
/// </summary>
public sealed record RecipeDiversityFacetValue(
    string Key,
    string DisplayName,
    Guid? TagId,
    RecipeDiversityEvidenceSource Source);

/// <summary>
/// Compact deterministic recipe-comparison facts. Every facet retains all values, so recipes with multiple
/// proteins or cuisines are not collapsed into a lossy primary classification. Missing semantic facets are
/// valid and report <see cref="RecipeDiversityConfidence.Missing"/>.
/// </summary>
public sealed record RecipeDiversityProfile(
    IReadOnlyList<RecipeDiversityFacetValue> ExactRecipe,
    IReadOnlyList<RecipeDiversityFacetValue> Diet,
    IReadOnlyList<RecipeDiversityFacetValue> Protein,
    IReadOnlyList<RecipeDiversityFacetValue> Cuisine,
    IReadOnlyList<RecipeDiversityFacetValue> Flavor)
{
    private static readonly IReadOnlyDictionary<string, string[]> ProteinAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tofu"] = ["tofu", "bean curd"],
            ["legumes"] =
            [
                "legume", "legumes", "bean", "beans", "lentil", "lentils", "chickpea", "chickpeas",
                "edamame", "split pea", "split peas",
            ],
        };

    /// <summary>
    /// Builds the profile from user-applied tags and confirmed recipe/Catalog facts. Protein and Cuisine
    /// fallback runs only when that categorized tag facet is absent. It matches the household's own active
    /// vocabulary deterministically; the small plant-protein alias set only broadens literal matching for the
    /// seeded Tofu/Legumes vocabulary and never infers a Diet stance.
    /// </summary>
    public static RecipeDiversityProfile Create(
        Guid recipeId,
        string recipeName,
        IReadOnlyList<RecipeSemanticTagFact> appliedTags,
        IReadOnlyList<RecipeSemanticTagFact> activeVocabulary,
        IReadOnlyList<RecipeIngredientFact> ingredients)
    {
        var exact = new[]
        {
            new RecipeDiversityFacetValue(
                RecipeKey(recipeId), recipeName, TagId: null, RecipeDiversityEvidenceSource.ConfirmedRecipeFact),
        };

        var diet = ConfirmedValues(appliedTags, RecipeSemanticTagCategory.Diet);
        var protein = ConfirmedValues(appliedTags, RecipeSemanticTagCategory.Protein);
        var cuisine = ConfirmedValues(appliedTags, RecipeSemanticTagCategory.Cuisine);
        var flavor = ConfirmedValues(appliedTags, RecipeSemanticTagCategory.Flavor);

        if (protein.Count == 0)
        {
            protein = FallbackValues(
                recipeName,
                ingredients,
                activeVocabulary,
                RecipeSemanticTagCategory.Protein,
                includeProteinAliases: true);
        }

        if (cuisine.Count == 0)
        {
            cuisine = FallbackValues(
                recipeName,
                ingredients,
                activeVocabulary,
                RecipeSemanticTagCategory.Cuisine,
                includeProteinAliases: false);
        }

        return new RecipeDiversityProfile(exact, diet, protein, cuisine, flavor);
    }

    public IReadOnlyList<RecipeDiversityFacetValue> Values(RecipeDiversityFacet facet) => facet switch
    {
        RecipeDiversityFacet.ExactRecipe => ExactRecipe,
        RecipeDiversityFacet.Diet => Diet,
        RecipeDiversityFacet.Protein => Protein,
        RecipeDiversityFacet.Cuisine => Cuisine,
        RecipeDiversityFacet.Flavor => Flavor,
        _ => throw new ArgumentOutOfRangeException(nameof(facet), facet, null),
    };

    public RecipeDiversityConfidence Confidence(RecipeDiversityFacet facet)
    {
        var values = Values(facet);
        if (values.Count == 0) return RecipeDiversityConfidence.Missing;
        if (facet == RecipeDiversityFacet.ExactRecipe) return RecipeDiversityConfidence.Confirmed;
        return values.Any(v => v.Source == RecipeDiversityEvidenceSource.ConfirmedTag)
            ? RecipeDiversityConfidence.Confirmed
            : RecipeDiversityConfidence.Fallback;
    }

    /// <summary>True when the two profiles share at least one stable value in the selected facet.</summary>
    public bool Shares(RecipeDiversityProfile other, RecipeDiversityFacet facet)
    {
        ArgumentNullException.ThrowIfNull(other);
        var keys = Values(facet).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        return keys.Count > 0 && other.Values(facet).Any(v => keys.Contains(v.Key));
    }

    private static IReadOnlyList<RecipeDiversityFacetValue> ConfirmedValues(
        IReadOnlyList<RecipeSemanticTagFact> tags,
        RecipeSemanticTagCategory category) => tags
        .Where(t => t.Category == category)
        .GroupBy(t => t.TagId)
        .Select(g => g.First())
        .OrderBy(t => t.TagId)
        .Select(t => new RecipeDiversityFacetValue(
            TagKey(t.TagId), t.DisplayName, t.TagId, RecipeDiversityEvidenceSource.ConfirmedTag))
        .ToList();

    private static IReadOnlyList<RecipeDiversityFacetValue> FallbackValues(
        string recipeName,
        IReadOnlyList<RecipeIngredientFact> ingredients,
        IReadOnlyList<RecipeSemanticTagFact> vocabulary,
        RecipeSemanticTagCategory category,
        bool includeProteinAliases)
    {
        var result = new List<RecipeDiversityFacetValue>();
        foreach (var tag in vocabulary
                     .Where(t => t.Category == category)
                     .GroupBy(t => t.TagId)
                     .Select(g => g.First())
                     .OrderBy(t => t.TagId))
        {
            var terms = MatchTerms(tag.DisplayName, includeProteinAliases);
            var catalogMatch = ingredients.Any(i => terms.Any(term => ContainsPhrase(i.DisplayName, term)));
            var recipeMatch = terms.Any(term => ContainsPhrase(recipeName, term));
            if (!catalogMatch && !recipeMatch) continue;

            result.Add(new RecipeDiversityFacetValue(
                TagKey(tag.TagId),
                tag.DisplayName,
                tag.TagId,
                catalogMatch
                    ? RecipeDiversityEvidenceSource.ConfirmedCatalogFact
                    : RecipeDiversityEvidenceSource.ConfirmedRecipeFact));
        }

        return result;
    }

    private static IReadOnlyList<string> MatchTerms(string displayName, bool includeProteinAliases)
    {
        var normalized = Normalize(displayName);
        if (includeProteinAliases && ProteinAliases.TryGetValue(normalized, out var aliases))
            return aliases;
        return [displayName];
    }

    private static bool ContainsPhrase(string value, string phrase)
    {
        var normalizedValue = $" {Normalize(value)} ";
        var normalizedPhrase = Normalize(phrase);
        return normalizedPhrase.Length > 0
               && normalizedValue.Contains($" {normalizedPhrase} ", StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        return string.Join(' ', new string(normalized).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string RecipeKey(Guid recipeId) => $"recipe:{recipeId:N}";
    private static string TagKey(Guid tagId) => $"tag:{tagId:N}";
}
