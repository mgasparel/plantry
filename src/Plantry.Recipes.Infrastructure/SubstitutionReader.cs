using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Application;

namespace Plantry.Recipes.Infrastructure;

/// <summary>
/// Production implementation of <see cref="ISubstitutionReader"/> over <see cref="RecipesDbContext"/>
/// (plantry-aqpa.1). Both directions are household-scoped by the <c>Substitution</c> entity's own EF
/// query filter — no explicit household predicate needed here.
/// </summary>
public sealed class SubstitutionReader(RecipesDbContext db) : ISubstitutionReader
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>>> ListByTargetProductIdsAsync(
        IReadOnlyList<Guid> targetProductIds, CancellationToken ct = default)
    {
        if (targetProductIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<SubstitutionEdge>>();

        var wanted = targetProductIds.Distinct().ToList();
        var rows = await db.Substitutions
            .Where(s => wanted.Contains(s.TargetProductId))
            .ToListAsync(ct);

        return rows
            .GroupBy(s => s.TargetProductId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<SubstitutionEdge> (g) => g.Select(ToEdge).ToList());
    }

    public async Task<IReadOnlyList<SubstitutionEdge>> ListTouchingProductAsync(
        Guid productId, CancellationToken ct = default)
    {
        var rows = await db.Substitutions
            .Where(s => s.TargetProductId == productId || s.SubstituteProductId == productId)
            .ToListAsync(ct);

        return rows.Select(ToEdge).ToList();
    }

    private static SubstitutionEdge ToEdge(Domain.Substitution s) => new(
        s.Id.Value,
        s.TargetProductId, s.TargetQuantity, s.TargetUnitId,
        s.SubstituteProductId, s.SubstituteQuantity, s.SubstituteUnitId);
}
