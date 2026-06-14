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

    Task AddAsync(Tag tag, CancellationToken ct = default);
}
