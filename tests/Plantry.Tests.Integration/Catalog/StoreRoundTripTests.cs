using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L2/L3 integration tests for the DM-16 <c>catalog.store</c> table: the aggregate + repository
/// round-trip through EF (incl. <c>FindByExternalRefAsync</c>), Postgres RLS isolates it per
/// household exactly like the other catalog tables, and both the <c>UNIQUE (household_id, name)</c>
/// and the partial <c>UNIQUE (household_id, external_ref) WHERE external_ref IS NOT NULL</c>
/// constraints are enforced at the database level.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StoreRoundTripTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Store round-trips through EF with name/external_ref/timestamps preserved")]
    public async Task Store_RoundTrips_Through_EfMapping()
    {
        StoreId id;
        await using (var db1 = NewCatalogDb(_household))
        {
            var store = Store.Create(_household, "FreshCo", Clock, "flipp-123");
            await db1.Stores.AddAsync(store);
            await db1.SaveChangesAsync();
            id = store.Id;
        }

        await using var db2 = NewCatalogDb(_household);
        var loaded = await db2.Stores.SingleAsync(s => s.Id == id);

        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal("FreshCo", loaded.Name);
        Assert.Equal("flipp-123", loaded.ExternalRef);
        Assert.False(loaded.IsArchived);
        Assert.NotEqual(default, loaded.CreatedAt);
    }

    [Fact(DisplayName = "Manual store persists with a null external_ref")]
    public async Task Store_ManualStore_Persists_Null_ExternalRef()
    {
        await using (var db1 = NewCatalogDb(_household))
        {
            await db1.Stores.AddAsync(Store.Create(_household, "Corner Market", Clock));
            await db1.SaveChangesAsync();
        }

        await using var db2 = NewCatalogDb(_household);
        var loaded = await db2.Stores.SingleAsync(s => s.Name == "Corner Market");
        Assert.Null(loaded.ExternalRef);
    }

    [Fact(DisplayName = "StoreRepository.FindByExternalRefAsync resolves the merchant by its directory id")]
    public async Task Repository_FindByExternalRef_Resolves_Merchant()
    {
        StoreId id;
        await using (var seedDb = NewCatalogDb(_household))
        {
            var repo = new StoreRepository(seedDb);
            var store = Store.Create(_household, "FreshCo", Clock, "flipp-123");
            await repo.AddAsync(store);
            await repo.SaveChangesAsync();
            id = store.Id;
        }

        await using var readDb = NewCatalogDb(_household);
        var repo2 = new StoreRepository(readDb);

        var byRef = await repo2.FindByExternalRefAsync("flipp-123");
        Assert.NotNull(byRef);
        Assert.Equal(id, byRef!.Id);

        var missing = await repo2.FindByExternalRefAsync("flipp-999");
        Assert.Null(missing);
    }

    [Fact(DisplayName = "Unique index rejects a duplicate (household_id, name) on stores")]
    public async Task UniqueIndex_Rejects_Duplicate_Store_Name_Within_Household()
    {
        await using var db1 = NewCatalogDb(_household);
        await db1.Stores.AddAsync(Store.Create(_household, "FreshCo", Clock));
        await db1.SaveChangesAsync();

        await using var db2 = NewCatalogDb(_household);
        await db2.Stores.AddAsync(Store.Create(_household, "FreshCo", Clock, "flipp-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact(DisplayName = "Partial unique index rejects a duplicate non-null (household_id, external_ref)")]
    public async Task PartialUniqueIndex_Rejects_Duplicate_ExternalRef_Within_Household()
    {
        await using var db1 = NewCatalogDb(_household);
        await db1.Stores.AddAsync(Store.Create(_household, "FreshCo", Clock, "flipp-1"));
        await db1.SaveChangesAsync();

        // Same external_ref, different name — must still collide on the partial unique index.
        await using var db2 = NewCatalogDb(_household);
        await db2.Stores.AddAsync(Store.Create(_household, "FreshCo West", Clock, "flipp-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact(DisplayName = "Partial unique index allows multiple null external_ref stores (manual stores excluded)")]
    public async Task PartialUniqueIndex_Allows_Multiple_Null_ExternalRef_Stores()
    {
        await using var db1 = NewCatalogDb(_household);
        await db1.Stores.AddAsync(Store.Create(_household, "Corner Market", Clock));
        await db1.Stores.AddAsync(Store.Create(_household, "Farm Stand", Clock));

        await db1.SaveChangesAsync(); // Two null external_refs must not collide.

        var count = await db1.Stores.CountAsync(s => s.ExternalRef == null);
        Assert.Equal(2, count);
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's stores")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Stores()
    {
        var householdB = HouseholdId.New();

        await using (var seedA = NewCatalogDb(_household))
        {
            await seedA.Stores.AddAsync(Store.Create(_household, "FreshCo", Clock, "flipp-1"));
            await seedA.SaveChangesAsync();
        }
        await using (var seedB = NewCatalogDb(householdB))
        {
            await seedB.Stores.AddAsync(Store.Create(householdB, "Loblaws", Clock, "flipp-2"));
            await seedB.SaveChangesAsync();
        }

        await using var readB = NewCatalogDb(householdB);
        var stores = await readB.Stores.ToListAsync();

        var own = Assert.Single(stores);
        Assert.Equal("Loblaws", own.Name);
        Assert.All(stores, s => Assert.Equal(householdB, s.HouseholdId));
    }

    [Fact(DisplayName = "Postgres RLS backstop: interceptor arms app.household_id; only own household's store rows visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsStoresToHousehold()
    {
        var householdB = HouseholdId.New();
        await SeedViaAppUserAsync(_household, "FreshCo", "flipp-1");
        await SeedViaAppUserAsync(householdB, "Loblaws", "flipp-2");

        var tenant = new TenantContext();
        tenant.Set(_household.Value);
        var opts = BuildCatalogOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var catalogDb = new CatalogDbContext(opts);

        var stores = await catalogDb.Stores.IgnoreQueryFilters().ToListAsync();

        var own = Assert.Single(stores);
        Assert.Equal(_household, own.HouseholdId);
        Assert.Equal("FreshCo", own.Name);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict catalog policy returns no store rows")]
    public async Task Interceptor_NoTenantContext_StrictCatalogPolicy_ReturnsNoStoreRows()
    {
        await SeedViaAppUserAsync(_household, "FreshCo", "flipp-1");

        var tenant = new TenantContext(); // never set
        var opts = BuildCatalogOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var catalogDb = new CatalogDbContext(opts);

        var stores = await catalogDb.Stores.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(stores);
    }

    private async Task SeedViaAppUserAsync(HouseholdId household, string name, string externalRef)
    {
        var tenant = new TenantContext();
        tenant.Set(household.Value);
        var opts = BuildCatalogOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var seedDb = new CatalogDbContext(opts);
        await seedDb.Stores.AddAsync(Store.Create(household, name, Clock, externalRef));
        await seedDb.SaveChangesAsync();
    }

    private DbContextOptions<CatalogDbContext> CatalogOptions() =>
        new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<CatalogDbContext> BuildCatalogOptions(string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var ctx = new CatalogDbContext(CatalogOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
