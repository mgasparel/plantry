using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L2 integration tests for <see cref="SetDefaultLocationCommand"/> (Take Stock P4-2 / TS-9).
/// Verifies against a real Postgres schema: sets default location; leaves other fields untouched;
/// rejects unknown location; RLS isolation — household B cannot affect household A's products.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SetDefaultLocationCommandTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private UnitId _gramsId;
    private ProductId _productId;
    private LocationId _pantryId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var seed = NewCatalogDb(_household);

        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await seed.Units.AddAsync(grams);

        var pantry = Location.Create(_household, "Pantry", LocationType.Ambient);
        await seed.Locations.AddAsync(pantry);

        var product = Product.Create(_household, "Oat Milk", grams.Id, SystemClock.Instance);
        product.SetCategory(CategoryId.New(), SystemClock.Instance);
        product.SetExpiryDefaults(7, 3, 90, 2, SystemClock.Instance);
        await seed.Products.AddAsync(product);

        await seed.SaveChangesAsync();

        _gramsId = grams.Id;
        _pantryId = pantry.Id;
        _productId = product.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "SetDefaultLocationCommand sets the location and persists it")]
    public async Task Sets_Location_And_Persists()
    {
        await using var ctx = NewCatalogDb(_household);
        var products = new ProductRepository(ctx);
        var locations = new LocationRepository(ctx);

        var result = await new SetDefaultLocationCommand(
            _productId, _pantryId.Value, products, locations, SystemClock.Instance).ExecuteAsync();

        Assert.True(result.IsSuccess);

        await using var verify = NewCatalogDb(_household);
        var loaded = await verify.Products.SingleAsync(p => p.Id == _productId);
        Assert.Equal(_pantryId, loaded.DefaultLocationId);
    }

    [Fact(DisplayName = "SetDefaultLocationCommand leaves name, unit, category, and expiry defaults untouched")]
    public async Task Does_Not_Clobber_Other_Fields()
    {
        // Capture state before
        await using var before = NewCatalogDb(_household);
        var original = await before.Products.SingleAsync(p => p.Id == _productId);
        var originalName = original.Name;
        var originalUnitId = original.DefaultUnitId;
        var originalCategoryId = original.CategoryId;
        var originalDueDays = original.DefaultDueDays;

        // Run the command
        await using var ctx = NewCatalogDb(_household);
        var result = await new SetDefaultLocationCommand(
            _productId, _pantryId.Value, new ProductRepository(ctx), new LocationRepository(ctx), SystemClock.Instance).ExecuteAsync();
        Assert.True(result.IsSuccess);

        // Verify only DefaultLocationId changed
        await using var after = NewCatalogDb(_household);
        var loaded = await after.Products.SingleAsync(p => p.Id == _productId);
        Assert.Equal(_pantryId, loaded.DefaultLocationId);
        Assert.Equal(originalName, loaded.Name);
        Assert.Equal(originalUnitId, loaded.DefaultUnitId);
        Assert.Equal(originalCategoryId, loaded.CategoryId);
        Assert.Equal(originalDueDays, loaded.DefaultDueDays);
    }

    [Fact(DisplayName = "SetDefaultLocationCommand rejects a location not in the household")]
    public async Task Rejects_Unknown_Location()
    {
        await using var ctx = NewCatalogDb(_household);
        var result = await new SetDefaultLocationCommand(
            _productId, Guid.NewGuid(), new ProductRepository(ctx), new LocationRepository(ctx), SystemClock.Instance).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownLocation", result.Error.Code);

        // Product's location unchanged in DB
        await using var verify = NewCatalogDb(_household);
        var loaded = await verify.Products.SingleAsync(p => p.Id == _productId);
        Assert.Null(loaded.DefaultLocationId);
    }

    [Fact(DisplayName = "SetDefaultLocationCommand is RLS-scoped: household B cannot assign a location to household A's product")]
    public async Task Rls_Scoped_HouseholdB_Cannot_Touch_HouseholdA_Product()
    {
        // Household B has its own unit; it does not share household A's location or product.
        var householdB = HouseholdId.New();
        await using var seedB = NewCatalogDb(householdB);
        var gramsB = CatalogUnit.Create(householdB, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var pantryB = Location.Create(householdB, "Pantry B", LocationType.Ambient);
        await seedB.Units.AddAsync(gramsB);
        await seedB.Locations.AddAsync(pantryB);
        await seedB.SaveChangesAsync();

        // Household B tries to set a location on a product it cannot see (household A's)
        await using var ctxB = NewCatalogDb(householdB);
        var result = await new SetDefaultLocationCommand(
            _productId,           // household A's product — invisible to B via EF filter
            pantryB.Id.Value,     // household B's location
            new ProductRepository(ctxB),
            new LocationRepository(ctxB),
            SystemClock.Instance).ExecuteAsync();

        // EF query filter returns null for _productId in household B's context → NotFound
        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);

        // Household A's product is still untouched
        await using var verifyA = NewCatalogDb(_household);
        var productA = await verifyA.Products.SingleAsync(p => p.Id == _productId);
        Assert.Null(productA.DefaultLocationId);
    }

    private DbContextOptions<CatalogDbContext> CatalogOptions() =>
        new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var ctx = new CatalogDbContext(CatalogOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
