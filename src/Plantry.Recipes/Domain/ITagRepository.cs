using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

/// <summary>
/// The recipes tag store (recipes-domain-model.md §5/§7). Tags are a closed, household-curated
/// vocabulary managed in /Settings. The <c>AuthorRecipe</c> service resolves submitted
/// <see cref="TagId"/>s to existing household <see cref="Tag"/>s and never mints — unknown or
/// foreign ids are silently dropped. Household scoping is applied by the DbContext query filter /
/// RLS, matching <see cref="IRecipeRepository"/>.
/// </summary>
public interface ITagRepository
{
    /// <summary>Resolves a tag by exact (case-insensitive) name within the household; null when none exists.</summary>
    Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default);

    /// <summary>Loads a tag by its id within the household; null when not found.</summary>
    Task<Tag?> GetByIdAsync(TagId id, CancellationToken ct = default);

    /// <summary>
    /// Resolves the display names for a set of tag ids within the household — used by the recipe
    /// Detail page to render tag pills. Returns a dictionary keyed by <see cref="TagId"/> containing
    /// only the ids that exist in this household (RLS / query filter applies; missing ids are omitted).
    /// Archived tags ARE included so existing recipe references never go blank.
    /// </summary>
    Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(IReadOnlyList<TagId> ids, CancellationToken ct = default);

    Task AddAsync(Tag tag, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists tags in the household, ordered by name.
    /// When <paramref name="activeOnly"/> is <c>true</c>, archived tags are excluded — suitable for
    /// the tag picker and dietary-preferences UI. When <c>false</c> (default), all tags are returned —
    /// suitable for the admin Settings/Tags management page.
    /// Scoping is applied by the DbContext query filter / RLS. Returns an empty list when the
    /// household has no tags.
    /// </summary>
    Task<IReadOnlyList<Tag>> ListAllAsync(bool activeOnly = false, CancellationToken ct = default);
}
