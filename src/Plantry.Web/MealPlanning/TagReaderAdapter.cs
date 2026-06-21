using Plantry.MealPlanning.Application;
using Plantry.Recipes.Domain;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="ITagReader"/> — supplies the MealPlanning context with tag
/// names and cosmetic categories from the Recipes vocabulary (DM-20), over
/// <see cref="ITagRepository"/>. Lives in Plantry.Web so MealPlanning stays free of Recipes deps.
/// </summary>
public sealed class TagReaderAdapter(ITagRepository tagRepository) : ITagReader
{
    // Maps TagCategory enum values to the 4 CAT_HUE values from plan-data.js.
    private static readonly Dictionary<TagCategory, int> CategoryHues = new()
    {
        { TagCategory.Diet,    150 },
        { TagCategory.Protein,  28 },
        { TagCategory.Flavor,  330 },
        { TagCategory.Cuisine, 255 },
    };

    // Canonical category order (matches TAG_GROUPS in plan-data.js).
    private static readonly string[] CategoryOrder = ["Diet", "Protein", "Flavor", "Cuisine"];

    public async Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default)
    {
        // Active-only: archived tags should not appear in dietary preferences.
        var all = await tagRepository.ListAllAsync(activeOnly: true, ct);

        var grouped = all
            .GroupBy(t => t.Category?.ToString())
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<TagGroup>();

        // Emit in canonical order first.
        foreach (var catName in CategoryOrder)
        {
            if (!grouped.TryGetValue(catName, out var tags)) continue;

            var hue = Enum.TryParse<TagCategory>(catName, out var cat) ? CategoryHues[cat] : (int?)null;
            result.Add(new TagGroup(
                catName,
                hue,
                tags.OrderBy(t => t.Name)
                    .Select(t => new TagSummary(t.Id.Value, t.Name, t.Category?.ToString(), hue))
                    .ToList()
            ));
        }

        // Any tag with no category or an unrecognised category goes in "Uncategorized".
        var uncategorized = grouped
            .Where(kv => kv.Key is null || !CategoryOrder.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .OrderBy(t => t.Name)
            .ToList();

        if (uncategorized.Count > 0)
        {
            result.Add(new TagGroup(
                "Uncategorized",
                null,
                uncategorized.Select(t => new TagSummary(t.Id.Value, t.Name, null, null)).ToList()
            ));
        }

        return result;
    }
}
