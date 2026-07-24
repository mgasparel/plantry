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
            .Include(c => c.ProduceLines)
            .Where(c => c.ConsumeLines.Any(l => l.Status == CookConsumeLineStatus.Pending)
                || c.ProduceLines.Any(p => p.Status == CookProduceLineStatus.Pending))
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

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, RecipeId>> GetRecipeIdsByCookEventIdsAsync(
        IReadOnlyCollection<Guid> cookEventIds, CancellationToken ct = default)
    {
        if (cookEventIds.Count == 0)
            return new Dictionary<Guid, RecipeId>();

        var wanted = cookEventIds.Select(CookEventId.From).ToHashSet();
        var rows = await db.CookEvents
            .Where(c => wanted.Contains(c.Id))
            .Select(c => new { c.Id, c.RecipeId })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id.Value, r => r.RecipeId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLatestCookedAtByPlannedDishIdsAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        if (plannedDishIds.Count == 0)
            return new Dictionary<Guid, DateTimeOffset>();

        // Materialise to a list so EF translates the Contains as an ANY(@p) array predicate.
        var wanted = plannedDishIds as IReadOnlyList<Guid> ?? plannedDishIds.ToList();

        var rows = await db.CookEvents
            .Where(c => c.PlannedDishId != null && wanted.Contains(c.PlannedDishId!.Value))
            .Select(c => new { PlannedDishId = c.PlannedDishId!.Value, c.CookedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.PlannedDishId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.CookedAt));
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
