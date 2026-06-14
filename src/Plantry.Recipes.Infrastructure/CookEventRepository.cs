using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Infrastructure;

public sealed class CookEventRepository(RecipesDbContext db) : ICookEventRepository
{
    public async Task AddAsync(CookEvent cookEvent, CancellationToken ct = default) =>
        await db.CookEvents.AddAsync(cookEvent, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        await db.CookEvents
            .Where(c => c.RecipeId == recipeId)
            .OrderByDescending(c => c.CookedAt)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
