using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;

namespace Plantry.Catalog.Infrastructure;

public sealed class StoreRepository(CatalogDbContext db) : IStoreRepository
{
    public Task<Store?> FindAsync(StoreId id, CancellationToken ct = default) =>
        db.Stores.FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Store?> FindByNameAsync(string name, CancellationToken ct = default) =>
        db.Stores.FirstOrDefaultAsync(s => s.Name == name, ct);

    public Task<Store?> FindByExternalRefAsync(string externalRef, CancellationToken ct = default) =>
        db.Stores.FirstOrDefaultAsync(s => s.ExternalRef == externalRef, ct);

    public Task<List<Store>> ListAsync(CancellationToken ct = default) =>
        db.Stores.OrderBy(s => s.Name).ToListAsync(ct);

    public Task<List<Store>> ListActiveAsync(CancellationToken ct = default) =>
        db.Stores.Where(s => s.ArchivedAt == null).OrderBy(s => s.Name).ToListAsync(ct);

    public async Task AddAsync(Store store, CancellationToken ct = default) =>
        await db.Stores.AddAsync(store, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
