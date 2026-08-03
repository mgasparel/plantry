using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Infrastructure;

public sealed class RecipeRatingRepository(RecipesDbContext db) : IRecipeRatingRepository
{
    public async Task AddAsync(RecipeRating rating, CancellationToken ct = default) =>
        await db.RecipeRatings.AddAsync(rating, ct);

    public void Remove(RecipeRating rating) => db.RecipeRatings.Remove(rating);

    public Task<RecipeRating?> FindAsync(RecipeId recipeId, Guid userId, CancellationToken ct = default) =>
        db.RecipeRatings.SingleOrDefaultAsync(r => r.RecipeId == recipeId && r.UserId == userId, ct);

    public async Task<IReadOnlyList<RecipeRating>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        await db.RecipeRatings.Where(r => r.RecipeId == recipeId).ToListAsync(ct);

    public async Task<IReadOnlyList<RecipeRating>> ListByRecipeIdsAsync(
        IReadOnlyList<RecipeId> recipeIds, CancellationToken ct = default)
    {
        if (recipeIds.Count == 0) return [];
        var idList = recipeIds.ToList();
        return await db.RecipeRatings.Where(r => idList.Contains(r.RecipeId)).ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
