using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Tenancy;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Catalog.Application;

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

internal sealed class FakeUnitRepository : IUnitRepository
{
    public List<CatalogUnit> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(u => u.Id == id));

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) => Task.FromResult(Items.ToList());

    public Task AddAsync(CatalogUnit unit, CancellationToken ct = default)
    {
        Items.Add(unit);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeCategoryRepository : ICategoryRepository
{
    public List<Category> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(c => c.Id == id));

    public Task<Category?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public Task<List<Category>> ListAsync(CancellationToken ct = default) => Task.FromResult(Items.ToList());

    public Task<List<Category>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(c => !c.IsArchived).ToList());

    public Task AddAsync(Category category, CancellationToken ct = default)
    {
        Items.Add(category);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeLocationRepository : ILocationRepository
{
    public List<Location> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<Location?> FindAsync(LocationId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(l => l.Id == id));

    public Task<Location?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public Task<List<Location>> ListAsync(CancellationToken ct = default) => Task.FromResult(Items.ToList());

    public Task<List<Location>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(l => !l.IsArchived).ToList());

    public Task AddAsync(Location location, CancellationToken ct = default)
    {
        Items.Add(location);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}
