namespace Plantry.Catalog.Domain;

public interface IUnitRepository
{
    Task<Unit?> FindAsync(UnitId id, CancellationToken ct = default);
    Task<Unit?> FindByCodeAsync(string code, CancellationToken ct = default);
    Task<List<Unit>> ListAsync(CancellationToken ct = default);
    Task AddAsync(Unit unit, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
