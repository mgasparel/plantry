using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Pantry.Application;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 pin for the CatalogReadFacade units N+1 fix (plantry-47tc, plantry-hw39 code review):
/// before <see cref="UnitCodesAccessor"/> existed, <c>CatalogReadFacade.FindProductAsync</c> loaded
/// the whole <c>catalog.units</c> table on every call, and <c>InventoryStockReaderAdapter</c>
/// (Plantry.Composition/Recipes) already calls <c>FindProductAsync</c> in a per-product loop — so a
/// recipe/meal-plan fulfilment read multiplied <c>units</c> reads by product count. This test
/// reproduces that exact shape (N <c>FindProductAsync</c> calls against a real Postgres
/// <c>catalog.units</c> table) and asserts exactly ONE <c>units</c> query is issued for the whole
/// batch, mirroring <c>HouseholdExpiryDefaultsAccessorQueryCountTests</c>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class UnitCodesAccessorQueryCountTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly List<ProductId> _productIds = [];

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalogDb = NewCatalogDb();
        var grams = Unit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await catalogDb.Units.AddAsync(grams);
        await catalogDb.SaveChangesAsync();

        for (var i = 0; i < 3; i++)
        {
            var product = Product.Create(_household, $"Product {i}", grams.Id, SystemClock.Instance);
            await catalogDb.Products.AddAsync(product);
            _productIds.Add(product.Id);
        }
        await catalogDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "FindProductAsync for 3 products through the SAME accessor issues exactly ONE units query (N+1 fix)")]
    public async Task FindProductAsync_ThreeProducts_IssuesOneUnitsQuery()
    {
        var counter = new QueryCountingInterceptor();
        await using var catalogDb = NewCatalogDb(counter);

        var facade = NewCatalogReadFacade(catalogDb);

        // Mirrors InventoryStockReaderAdapter's per-product FindProductAsync loop (the amplification
        // point named in the original finding) — all three calls share the one request-scoped facade.
        foreach (var productId in _productIds)
        {
            var info = await facade.FindProductAsync(productId.Value);
            Assert.NotNull(info);
        }

        Assert.Equal(1, counter.CountMatching("units"));
    }

    [Fact(DisplayName = "A new request scope (new accessor instance) re-resolves — the cache is per-request, not static/shared")]
    public async Task NewScope_ReResolves_UnitsQuery()
    {
        var counter = new QueryCountingInterceptor();

        await using (var catalogDb1 = NewCatalogDb(counter))
        {
            var facade1 = NewCatalogReadFacade(catalogDb1);
            await facade1.FindProductAsync(_productIds[0].Value);
            Assert.Equal(1, counter.CountMatching("units"));
        }

        await using (var catalogDb2 = NewCatalogDb(counter))
        {
            var facade2 = NewCatalogReadFacade(catalogDb2);
            await facade2.FindProductAsync(_productIds[1].Value);
            Assert.Equal(2, counter.CountMatching("units"));
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private CatalogReadFacade NewCatalogReadFacade(CatalogDbContext catalogDb) =>
        new(
            new ProductRepository(catalogDb), new UnitCodesAccessor(new UnitRepository(catalogDb)),
            new CategoryRepository(catalogDb), new LocationRepository(catalogDb),
            new FakeHouseholdExpiryDefaultsReader());

    private CatalogDbContext NewCatalogDb(QueryCountingInterceptor? counter = null)
    {
        var builder = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString);
        if (counter is not null) builder.AddInterceptors(counter);
        var ctx = new CatalogDbContext(builder.Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
