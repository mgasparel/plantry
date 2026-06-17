using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IRecipeReadModel"/> — supplies the MealPlanning context with
/// recipe display facts from the Recipes context, over <see cref="RecipesDbContext"/>.
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Recipes dependencies.
/// </summary>
public sealed class RecipeReadModelAdapter(RecipesDbContext db) : IRecipeReadModel
{
    public async Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
    {
        // Use the strongly-typed RecipeId so EF Core's value converter can translate the predicate.
        // Accessing .Value directly on a converted type in a LINQ predicate causes a translation
        // failure when combined with a HasQueryFilter that also uses a converted type.
        var rid = RecipeId.From(recipeId);
        var recipe = await db.Recipes
            .Include(r => r.Tags)
            .FirstOrDefaultAsync(r => r.Id == rid && r.ArchivedAt == null, ct);

        if (recipe is null) return null;

        return new RecipeReadModel(
            recipe.Id.Value,
            recipe.Name,
            recipe.Tags.Select(t => t.TagId.Value).ToList(),
            recipe.DefaultServings);
    }

    public async Task<IReadOnlyList<RecipeReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(nameQuery) ? "" : nameQuery.Trim();

        var recipes = await db.Recipes
            .Include(r => r.Tags)
            .Where(r => r.ArchivedAt == null &&
                        (q == "" || EF.Functions.ILike(r.Name, $"%{q}%")))
            .OrderBy(r => r.Name)
            .Take(maxResults)
            .ToListAsync(ct);

        return recipes.Select(r => new RecipeReadModel(
            r.Id.Value,
            r.Name,
            r.Tags.Select(t => t.TagId.Value).ToList(),
            r.DefaultServings)).ToList();
    }
}
