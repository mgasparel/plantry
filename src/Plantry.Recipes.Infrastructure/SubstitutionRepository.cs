using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Infrastructure;

public sealed class SubstitutionRepository(RecipesDbContext db) : ISubstitutionRepository
{
    public async Task AddAsync(Substitution substitution, CancellationToken ct = default) =>
        await db.Substitutions.AddAsync(substitution, ct);

    public void Remove(Substitution substitution) => db.Substitutions.Remove(substitution);

    public Task<Substitution?> GetByIdAsync(SubstitutionId id, CancellationToken ct = default) =>
        db.Substitutions.SingleOrDefaultAsync(s => s.Id == id, ct);

    public Task<Substitution?> FindByPairAsync(
        Guid substituteProductId, Guid targetProductId, CancellationToken ct = default) =>
        db.Substitutions.SingleOrDefaultAsync(
            s => s.SubstituteProductId == substituteProductId && s.TargetProductId == targetProductId, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
