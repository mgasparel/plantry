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

    public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
        db.Products
            .Include(p => p.Conversions)
            .Where(p => p.ParentProductId == parentId)
            .ToListAsync(ct);

    public async Task AddAsync(Product product, CancellationToken ct = default) =>
        await db.Products.AddAsync(product, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
