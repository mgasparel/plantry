using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;

namespace Plantry.Catalog.Infrastructure;

public sealed class ProductRepository(CatalogDbContext db) : IProductRepository
{
    public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
        db.Products
            .Include(p => p.Skus)
            .Include(p => p.Conversions)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
        db.Products.FirstOrDefaultAsync(p => p.Name == name, ct);

    public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
        db.Products.Where(p => p.ArchivedAt == null).OrderBy(p => p.Name).ToListAsync(ct);

    public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
        db.Products.Include(p => p.Skus).Where(p => p.ArchivedAt == null).OrderBy(p => p.Name).ToListAsync(ct);

    public Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        return db.Products.Include(p => p.Conversions).Where(p => idList.Contains(p.Id)).ToListAsync(ct);
    }

    public Task<List<Product>> ListByIdsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        return db.Products.Where(p => idList.Contains(p.Id)).ToListAsync(ct);
    }

    public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
        db.Products
            .Include(p => p.Conversions)
            .Where(p => p.ParentProductId == parentId)
            .ToListAsync(ct);

    public Task<List<Product>> ListWithVariantsAsync(IReadOnlyList<ProductId> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        // One query for the requested products plus every variant child of any requested parent, so the
        // DM-19 rollup gets the whole tree without a per-parent round-trip. The variant match uses a
        // correlated EXISTS with an equality on ParentProductId (the shape ListVariantsAsync proves
        // translatable) rather than an IN over ParentProductId — Npgsql cannot build an array mapping
        // for the value-converted nullable FK, so `ParentProductId IN (...)` fails to translate. The
        // fulfillment DTO needs only ids/name/track_stock/units/archived flag — no Skus/Conversions.
        return db.Products
            .Where(p => idList.Contains(p.Id)
                     || db.Products.Any(parent => idList.Contains(parent.Id) && parent.Id == p.ParentProductId))
            .ToListAsync(ct);
    }

    public async Task AddAsync(Product product, CancellationToken ct = default) =>
        await db.Products.AddAsync(product, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
