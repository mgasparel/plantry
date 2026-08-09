using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Finds active recipes whose user-maintained diversity metadata is incomplete. This is a maintenance
/// read only: it never classifies, applies, or mints a tag. The Settings/Tags surface links each result to
/// the existing recipe editor, where manual edits and AI proposals retain their normal confirmation step.
/// </summary>
public sealed class RecipeDiversityMetadataQuery(
    IRecipeRepository recipes,
    ITagRepository tags)
{
    private static readonly TagCategory[] MaintainedFacets = [TagCategory.Protein, TagCategory.Cuisine];

    public async Task<IReadOnlyList<RecipeDiversityMetadataGap>> ExecuteAsync(CancellationToken ct = default)
    {
        var recipeRows = await recipes.ListForBrowseAsync(ct);
        if (recipeRows.Count == 0) return [];

        // Include archived tags because an existing recipe's explicit metadata remains authoritative even
        // after that vocabulary entry stops appearing in pickers.
        var categoryById = (await tags.ListAllAsync(activeOnly: false, ct))
            .ToDictionary(t => t.Id, t => t.Category);

        return recipeRows
            .Select(recipe =>
            {
                var present = recipe.Tags
                    .Select(rt => categoryById.GetValueOrDefault(rt.TagId))
                    .Where(category => category.HasValue)
                    .Select(category => category!.Value)
                    .ToHashSet();
                IReadOnlyList<TagCategory> missing = MaintainedFacets
                    .Where(category => !present.Contains(category))
                    .ToList();
                return new RecipeDiversityMetadataGap(recipe.Id, recipe.Name, missing);
            })
            .Where(gap => gap.MissingCategories.Count > 0)
            .OrderBy(gap => gap.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(gap => gap.RecipeId.Value)
            .ToList();
    }
}

/// <summary>One recipe and the semantic categories requiring user maintenance.</summary>
public sealed record RecipeDiversityMetadataGap(
    RecipeId RecipeId,
    string Name,
    IReadOnlyList<TagCategory> MissingCategories);
