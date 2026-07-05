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

    /// <inheritdoc />
    public async Task<IReadOnlyList<CookEvent>> ListWithPendingLinesAsync(CancellationToken ct = default) =>
        await db.CookEvents
            .Include(c => c.ConsumeLines)
            .Where(c => c.ConsumeLines.Any(l => l.Status == CookConsumeLineStatus.Pending))
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CookEvent>> ListWithDeferredUnitGapLinesForProductsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return [];

        // Materialise to a list so EF translates the Contains as an ANY(@p) array predicate.
        var ids = productIds as IReadOnlyList<Guid> ?? productIds.ToList();

        return await db.CookEvents
            .Include(c => c.ConsumeLines)
            .Where(c => c.ConsumeLines.Any(l =>
                l.Status == CookConsumeLineStatus.DeferredUnitGap && ids.Contains(l.ProductId)))
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
