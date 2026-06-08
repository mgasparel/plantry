using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;

namespace Plantry.Catalog.Infrastructure;

public sealed class UnitRepository(CatalogDbContext db) : IUnitRepository
{
    public Task<Unit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        db.Units.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<Unit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        db.Units.FirstOrDefaultAsync(u => u.Code == code, ct);

    public Task<List<Unit>> ListAsync(CancellationToken ct = default) =>
        db.Units.OrderBy(u => u.Dimension).ThenBy(u => u.Name).ToListAsync(ct);

    public async Task AddAsync(Unit unit, CancellationToken ct = default) =>
        await db.Units.AddAsync(unit, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
