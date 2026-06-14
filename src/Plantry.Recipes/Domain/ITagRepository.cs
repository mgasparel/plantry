using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

/// <summary>
/// The recipes tag store (recipes-domain-model.md §5/§7). Backs inline tag minting from the editor
/// (J6): the <c>AuthorRecipe</c> service resolves each typed tag name to an existing <see cref="Tag"/>
/// or mints a new one via <see cref="Tag.Create"/> before <c>Recipe.SetTags</c>. Household scoping is
/// applied by the DbContext query filter / RLS, matching <see cref="IRecipeRepository"/>.
/// </summary>
public interface ITagRepository
{
    /// <summary>Resolves a tag by exact (case-insensitive) name within the household; null when none exists.</summary>
    Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default);

    /// <summary>
    /// Resolves the display names for a set of tag ids within the household — used by the recipe
    /// Detail page to render tag pills. Returns a dictionary keyed by <see cref="TagId"/> containing
    /// only the ids that exist in this household (RLS / query filter applies; missing ids are omitted).
    /// </summary>
    Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(IReadOnlyList<TagId> ids, CancellationToken ct = default);

    Task AddAsync(Tag tag, CancellationToken ct = default);

    /// <summary>
    /// Lists all tags in the household — used by the Browse page to populate the tag filter chips.
    /// Scoping is applied by the DbContext query filter / RLS. Returns an empty list when the
    /// household has no tags.
    /// </summary>
    Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct = default);
}
