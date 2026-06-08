using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L3 integration tests proving the Stage-A reference-data refactor round-trips through EF
/// and that the per-household uniqueness constraints (PHASE-1-PLAN.md A4 / catalog.md) are
/// enforced at the database level — not just in the application layer.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogReferenceDataTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Unit round-trips through EF with code/name/dimension/factor preserved")]
    public async Task Unit_RoundTrips_Through_EfMapping()
    {
        await using (var db1 = NewCatalogDb())
        {
            var unit = CatalogUnit.Create(_household, "bunch", "bunch", Dimension.Count, 1m);
            await db1.Units.AddAsync(unit);
            await db1.SaveChangesAsync();
        }

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Units.SingleAsync(u => u.Code == "bunch");

        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal("bunch", loaded.Name);
        Assert.Equal(Dimension.Count, loaded.Dimension);
        Assert.Equal(1m, loaded.FactorToBase);
        Assert.False(loaded.IsBase);
    }

    [Fact(DisplayName = "Category round-trips through EF with default-due-days and sort-order preserved")]
    public async Task Category_RoundTrips_Through_EfMapping()
    {
        await using (var db1 = NewCatalogDb())
        {
            var category = Category.Create(_household, "Bakery", defaultDueDays: 4, sortOrder: 99);
            await db1.Categories.AddAsync(category);
            await db1.SaveChangesAsync();
        }

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Categories.SingleAsync(c => c.Name == "Bakery");

        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal(4, loaded.DefaultDueDays);
        Assert.Equal(99, loaded.SortOrder);
    }

    [Fact(DisplayName = "Location round-trips through EF with type preserved")]
    public async Task Location_RoundTrips_Through_EfMapping()
    {
        await using (var db1 = NewCatalogDb())
        {
            var location = Location.Create(_household, "Garage freezer", LocationType.Frozen);
            await db1.Locations.AddAsync(location);
            await db1.SaveChangesAsync();
        }

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Locations.SingleAsync(l => l.Name == "Garage freezer");

        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal(LocationType.Frozen, loaded.Type);
        Assert.True(loaded.IsFrozen);
    }

    [Fact(DisplayName = "Unique index rejects a duplicate (household_id, code) on units")]
    public async Task UniqueIndex_Rejects_Duplicate_Unit_Code_Within_Household()
    {
        await using var db1 = NewCatalogDb();
        await db1.Units.AddAsync(CatalogUnit.Create(_household, "bunch", "bunch", Dimension.Count, 1m));
        await db1.SaveChangesAsync();

        await using var db2 = NewCatalogDb();
        await db2.Units.AddAsync(CatalogUnit.Create(_household, "bunch", "Bunch (duplicate)", Dimension.Count, 1m));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact(DisplayName = "Unique index rejects a duplicate (household_id, name) on categories")]
    public async Task UniqueIndex_Rejects_Duplicate_Category_Name_Within_Household()
    {
        await using var db1 = NewCatalogDb();
        await db1.Categories.AddAsync(Category.Create(_household, "Bakery"));
        await db1.SaveChangesAsync();

        await using var db2 = NewCatalogDb();
        await db2.Categories.AddAsync(Category.Create(_household, "Bakery", defaultDueDays: 10));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact(DisplayName = "Unique index rejects a duplicate (household_id, name) on locations")]
    public async Task UniqueIndex_Rejects_Duplicate_Location_Name_Within_Household()
    {
        await using var db1 = NewCatalogDb();
        await db1.Locations.AddAsync(Location.Create(_household, "Garage freezer", LocationType.Frozen));
        await db1.SaveChangesAsync();

        await using var db2 = NewCatalogDb();
        await db2.Locations.AddAsync(Location.Create(_household, "Garage freezer", LocationType.Ambient));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact(DisplayName = "Unique index allows the same name across different households")]
    public async Task UniqueIndex_Allows_Same_Name_Across_Different_Households()
    {
        var otherHousehold = HouseholdId.New();

        await using var db1 = NewCatalogDb();
        await db1.Locations.AddAsync(Location.Create(_household, "Pantry", LocationType.Ambient));
        await db1.Locations.AddAsync(Location.Create(otherHousehold, "Pantry", LocationType.Ambient));

        await db1.SaveChangesAsync(); // Should not throw — names collide only within a household.

        var names = await db1.Locations.IgnoreQueryFilters()
            .Where(l => l.HouseholdId == _household || l.HouseholdId == otherHousehold)
            .Select(l => l.Name)
            .ToListAsync();
        Assert.Equal(2, names.Count(n => n == "Pantry"));
    }

    private DbContextOptions<CatalogDbContext> CatalogOptions() =>
        new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(CatalogOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
