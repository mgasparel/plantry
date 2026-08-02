using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 real-Postgres regression guard for <see cref="CatalogReadFacade.ListProductsAsync"/>. A
/// product list containing several variants must resolve their shared parent in one batched
/// Catalog query; a per-variant <c>FindAsync</c> would make the product-query count grow with the
/// number of variants while the other reference-data queries stay unchanged.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogReadFacadeQueryCountTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalogDb = NewCatalogDb();
        var grams = Unit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var parent = Product.Create(_household, "Shared parent", grams.Id, SystemClock.Instance);
        parent.SetHasVariants(true, SystemClock.Instance);

        await catalogDb.Units.AddAsync(grams);
        await catalogDb.Products.AddAsync(parent);
        await catalogDb.SaveChangesAsync();

        for (var i = 0; i < 3; i++)
        {
            var variant = Product.Create(_household, $"Variant {i}", grams.Id, SystemClock.Instance);
            variant.MakeVariantOf(parent.Id, SystemClock.Instance);
            await catalogDb.Products.AddAsync(variant);
        }

        await catalogDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "ListProductsAsync resolves three variants with one batched parent lookup")]
    public async Task ListProductsAsync_MultipleVariants_UsesOneBatchedParentQuery()
    {
        var counter = new QueryCountingInterceptor();
        await using var catalogDb = NewCatalogDb(counter);
        var facade = new CatalogReadFacade(
            new ProductRepository(catalogDb),
            new UnitCodesAccessor(new UnitRepository(catalogDb)),
            new CategoryRepository(catalogDb),
            new LocationRepository(catalogDb),
            new FakeHouseholdExpiryDefaultsReader());

        var products = await facade.ListProductsAsync();

        Assert.Equal(4, products.Count);
        Assert.Equal(3, products.Count(p => p.IsVariant));

        // ListActiveAsync is the first product query; the only other product query must be the
        // single ListByIdsAsync parent batch. A per-variant parent fetch would produce four.
        var productQueries = counter.Commands
            .Where(c => c.Contains("products", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, productQueries.Count);
    }

    private CatalogDbContext NewCatalogDb(QueryCountingInterceptor? counter = null)
    {
        var builder = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString);
        if (counter is not null) builder.AddInterceptors(counter);

        var context = new CatalogDbContext(builder.Options);
        context.SetHouseholdId(_household.Value);
        return context;
    }
}
