using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Read-side query for the product→recipes cross-context view (plantry-o0r8) — the reverse of the
/// recipe→product ingredient soft-ref (DM-3/R4, <see cref="Ingredient"/>). Given a catalog product id,
/// returns every household recipe that directly references it, so a product's detail page can answer
/// "which recipes use this?" and "is this safe to stop stocking?".
///
/// <para>Two distinct relationships are surfaced per <see cref="RecipeProductUsage"/> row: a recipe with a
/// direct ingredient line referencing the product ("Used in", <see cref="RecipeProductUsage.IsConsumer"/>),
/// and a recipe whose declared cook yield targets the product ("Made by",
/// <see cref="RecipeProductUsage.IsProducer"/>, recipe-composition.md §9). v1 scope is DIRECT references
/// only — a product consumed exclusively by a sub-recipe that some other recipe includes is not surfaced
/// transitively through the inclusion graph (design decision, plantry-o0r8, deferred pending product
/// input); see <see cref="IRecipeRepository.ListRecipesReferencingProductAsync"/> for the read itself.
/// </para>
/// </summary>
public sealed class RecipesUsingProductQuery(IRecipeRepository recipes, ITenantContext tenant)
{
    public async Task<IReadOnlyList<RecipeProductUsage>> ExecuteAsync(Guid productId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
        {
            // Unauthenticated — caller should never reach here (authorization guard on page), but
            // return an empty result rather than throwing (mirrors BrowseRecipesQuery's guard).
            return [];
        }

        var refs = await recipes.ListRecipesReferencingProductAsync(productId, ct);
        return refs
            .Select(r => new RecipeProductUsage(r.RecipeId.Value, r.Name, r.IsConsumer, r.IsProducer))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// One household recipe's direct relationship to a product — <see cref="IsConsumer"/> when the recipe has
/// an ingredient line referencing the product ("Used in"); <see cref="IsProducer"/> when the recipe's
/// declared yield targets the product ("Made by"). Both may be true for the same row.
/// </summary>
public sealed record RecipeProductUsage(Guid RecipeId, string Name, bool IsConsumer, bool IsProducer);
