using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;

namespace Plantry.Catalog.Infrastructure;

public sealed class LocationRepository(CatalogDbContext db) : ILocationRepository
{
    public Task<Location?> FindAsync(LocationId id, CancellationToken ct = default) =>
        db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);

    public Task<Location?> FindByNameAsync(string name, CancellationToken ct = default) =>
        db.Locations.FirstOrDefaultAsync(l => l.Name == name, ct);

    public Task<List<Location>> ListAsync(CancellationToken ct = default) =>
        db.Locations.OrderBy(l => l.Name).ToListAsync(ct);

    public Task<List<Location>> ListActiveAsync(CancellationToken ct = default) =>
        db.Locations.Where(l => l.ArchivedAt == null).OrderBy(l => l.Name).ToListAsync(ct);

    public async Task AddAsync(Location location, CancellationToken ct = default) =>
        await db.Locations.AddAsync(location, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
