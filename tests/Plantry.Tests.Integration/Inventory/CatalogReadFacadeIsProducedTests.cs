using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Pantry.Application;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 real-Postgres guard for the <c>IsProduced</c> projection in
/// <see cref="CatalogReadFacade"/>'s <c>ToInfo</c> (plantry-sn6v). That projection line is the
/// single load-bearing link between the persisted domain flag and every consumer of
/// <c>CatalogProductInfo.IsProduced</c> — above all the shopping restock-candidate exclusion in
/// <c>ShoppingPantryReaderAdapter</c>, whose own tests run against a fake facade and so never
/// touch this mapping. Without this test, dropping the projection (defaulting the flag to false)
/// leaves every suite green while "buy your own leftovers" suggestions fully return.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogReadFacadeIsProducedTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalogDb = NewCatalogDb();
        var grams = Unit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var leftovers = Product.Create(
            _household, "Roast Chicken (leftovers)", grams.Id, SystemClock.Instance,
            trackStock: true, isProduced: true);
        var milk = Product.Create(_household, "Milk", grams.Id, SystemClock.Instance);

        await catalogDb.Units.AddAsync(grams);
        await catalogDb.Products.AddRangeAsync(leftovers, milk);
        await catalogDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "ListProductsAsync projects the persisted IsProduced flag through to CatalogProductInfo")]
    public async Task ListProductsAsync_ProjectsIsProduced_FromPersistedProducts()
    {
        await using var catalogDb = NewCatalogDb();
        var facade = new CatalogReadFacade(
            new ProductRepository(catalogDb),
            new UnitCodesAccessor(new UnitRepository(catalogDb)),
            new CategoryRepository(catalogDb),
            new LocationRepository(catalogDb),
            new FakeHouseholdExpiryDefaultsReader());

        var products = await facade.ListProductsAsync();

        Assert.True(products.Single(p => p.Name == "Roast Chicken (leftovers)").IsProduced);
        Assert.False(products.Single(p => p.Name == "Milk").IsProduced);
    }

    private PantryDbContext NewCatalogDb()
    {
        var builder = new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString);

        var context = new PantryDbContext(builder.Options);
        context.SetHouseholdId(_household.Value);
        return context;
    }
}
