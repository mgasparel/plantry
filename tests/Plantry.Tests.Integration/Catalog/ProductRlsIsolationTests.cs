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
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L3 integration tests proving Postgres RLS isolates the B4 product tables
/// (<c>product</c>, <c>product_sku</c>, <c>product_conversion</c>) exactly like the
/// Stage-A reference-data tables — household A physically cannot read household B's rows
/// (PHASE-1-PLAN.md Slice 1, Stage B done-when: "RLS isolation extended to product tables").
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ProductRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private ProductId _productA;
    private ProductId _productB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        _productA = await SeedHouseholdAsync(_householdA, "Flour");
        _productB = await SeedHouseholdAsync(_householdB, "Sugar");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ProductId> SeedHouseholdAsync(HouseholdId household, string productName)
    {
        await using var seedDb = NewCatalogDb(household);

        var grams = CatalogUnit.Create(household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var cups = CatalogUnit.Create(household, "cup", "cups", Dimension.Volume, 240m);
        await seedDb.Units.AddRangeAsync(grams, cups);

        var product = Product.Create(household, productName, grams.Id, SystemClock.Instance);
        product.AddSku("1 kg bag", 1000m, grams.Id, SystemClock.Instance);
        product.AddConversion(cups.Id, grams.Id, 120m, SystemClock.Instance);
        await seedDb.Products.AddAsync(product);

        await seedDb.SaveChangesAsync();
        return product.Id;
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's products")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Products()
    {
        await using var catalogDb = NewCatalogDb(_householdA);

        var products = await catalogDb.Products
            .Include(p => p.Skus)
            .Include(p => p.Conversions)
            .ToListAsync();

        Assert.All(products, p => Assert.Equal(_householdA, p.HouseholdId));
        Assert.DoesNotContain(products, p => p.Id == _productB);

        var own = Assert.Single(products);
        Assert.All(own.Skus, s => Assert.Equal(_householdA, s.HouseholdId));
        Assert.All(own.Conversions, c => Assert.Equal(_householdA, c.HouseholdId));
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with wrong app.household_id returns no product/sku/conversion rows")]
    public async Task RlsPolicy_RawSql_WithWrongHouseholdId_ReturnsNoRows()
    {
        // Bypass EF entirely — connect as the non-superuser app_user role and prove the
        // Postgres policies on all three product tables fire (RLS never applies to superusers).
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await AssertOnlyHouseholdVisibleAsync(conn, "catalog.products", _householdA.Value, _householdB.Value);
        await AssertOnlyHouseholdVisibleAsync(conn, "catalog.product_skus", _householdA.Value, _householdB.Value);
        await AssertOnlyHouseholdVisibleAsync(conn, "catalog.product_conversions", _householdA.Value, _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's product rows visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsProductTablesToHousehold()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildCatalogOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var catalogDb = new CatalogDbContext(opts);

        var products = await catalogDb.Products
            .IgnoreQueryFilters()
            .Include(p => p.Skus)
            .Include(p => p.Conversions)
            .ToListAsync();

        Assert.NotEmpty(products);
        Assert.All(products, p => Assert.Equal(_householdA, p.HouseholdId));
        Assert.DoesNotContain(products, p => p.Id == _productB);

        var own = Assert.Single(products);
        Assert.NotEmpty(own.Skus);
        Assert.NotEmpty(own.Conversions);
        Assert.All(own.Skus, s => Assert.Equal(_householdA, s.HouseholdId));
        Assert.All(own.Conversions, c => Assert.Equal(_householdA, c.HouseholdId));
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict catalog policy returns no product rows")]
    public async Task Interceptor_NoTenantContext_StrictCatalogPolicy_ReturnsNoProductRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildCatalogOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var catalogDb = new CatalogDbContext(opts);

        var products = await catalogDb.Products.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(products);
    }

    private static async Task AssertOnlyHouseholdVisibleAsync(
        NpgsqlConnection conn, string table, Guid expectedHouseholdId, Guid forbiddenHouseholdId)
    {
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = $"SELECT household_id FROM {table}";
        await using var reader = await selectCmd.ExecuteReaderAsync();

        var seenIds = new List<Guid>();
        while (await reader.ReadAsync())
            seenIds.Add(reader.GetGuid(0));

        Assert.NotEmpty(seenIds);
        Assert.All(seenIds, id => Assert.Equal(expectedHouseholdId, id));
        Assert.DoesNotContain(seenIds, id => id == forbiddenHouseholdId);
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
