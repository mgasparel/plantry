namespace Plantry.Recipes.Domain;

/// <summary>
/// Cosmetic grouping for a <see cref="Tag"/> — has <b>no planner meaning</b> (recipes-domain-model.md
/// §5, C2). Persisted as a <c>text</c>+<c>CHECK</c> enum per DataModels/conventions.md, mirroring the
/// string-enum convention used by Intake's <c>ImportStatus</c>.
/// </summary>
public enum TagCategory { Diet, Protein, Flavor, Cuisine }

public static class TagCategoryExtensions
{
    public static string ToDbValue(this TagCategory category) => category switch
    {
        TagCategory.Diet => "Diet",
        TagCategory.Protein => "Protein",
        TagCategory.Flavor => "Flavor",
        TagCategory.Cuisine => "Cuisine",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    public static TagCategory Parse(string value) => value switch
    {
        "Diet" => TagCategory.Diet,
        "Protein" => TagCategory.Protein,
        "Flavor" => TagCategory.Flavor,
        "Cuisine" => TagCategory.Cuisine,
        _ => throw new ArgumentException($"Unknown tag category '{value}'.", nameof(value)),
    };
}
