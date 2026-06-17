namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto the Recipes tag vocabulary (DM-20).
/// Supplies the MealPlanning context with the tag facts it needs — name and cosmetic category —
/// without taking a direct dependency on the Recipes bounded context.
/// Implemented in Plantry.Web over <c>ITagRepository</c>.
/// </summary>
public interface ITagReader
{
    /// <summary>
    /// Returns all tags for the household, grouped by cosmetic category (Diet, Protein, Flavor, Cuisine).
    /// Tags without a category are returned in the "Uncategorized" group.
    /// </summary>
    Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default);
}

/// <summary>A cosmetic group of tags for the preferences UI (matches TAG_GROUPS in plan-data.js).</summary>
public sealed record TagGroup(string Category, int? CategoryHue, IReadOnlyList<TagSummary> Tags);

/// <summary>The display slice of a Recipes tag needed for the preferences page.</summary>
public sealed record TagSummary(Guid TagId, string Name, string? Category, int? CategoryHue);
