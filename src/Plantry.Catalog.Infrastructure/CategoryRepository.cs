using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;

namespace Plantry.Catalog.Infrastructure;

public sealed class CategoryRepository(CatalogDbContext db) : ICategoryRepository
{
    public Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default) =>
        db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Category?> FindByNameAsync(string name, CancellationToken ct = default) =>
        db.Categories.FirstOrDefaultAsync(c => c.Name == name, ct);

    public Task<List<Category>> ListAsync(CancellationToken ct = default) =>
        db.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);

    public Task<List<Category>> ListActiveAsync(CancellationToken ct = default) =>
        db.Categories.Where(c => c.ArchivedAt == null).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);

    public async Task AddAsync(Category category, CancellationToken ct = default) =>
        await db.Categories.AddAsync(category, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
