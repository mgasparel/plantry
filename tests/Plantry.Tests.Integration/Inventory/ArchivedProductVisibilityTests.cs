using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;
using Xunit;
using CatalogProduct = Plantry.Catalog.Domain.Product;
using InventoryProductStock = Plantry.Inventory.Domain.ProductStock;
using CatalogCategoryRepository = Plantry.Catalog.Infrastructure.CategoryRepository;
using CatalogLocationRepository = Plantry.Catalog.Infrastructure.LocationRepository;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests for plantry-lxm2's Gap 2 fix — proving end-to-end against a real Postgres
/// schema that archiving a product no longer hides its still-on-hand stock: <c>ProductRepository</c>'s
/// new <see cref="IProductRepository.ListArchivedAsync"/> query, <c>CatalogReadFacade</c>'s new
/// <see cref="ICatalogReadFacade.ListArchivedProductsAsync"/> projection, and
/// <see cref="InventoryQueryService.ListPantryAsync"/>/<see cref="InventoryQueryService.CountInStockAsync"/>
/// all wired together for real, rather than through the unit-test fakes.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ArchivedProductVisibilityTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private Guid _productId;
    private Guid _unitId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalogDb = NewCatalogDb();
        var grams = Unit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await catalogDb.Units.AddAsync(grams);
        var product = CatalogProduct.Create(_household, "Instant espresso", grams.Id, Clock);
        product.Archive(Clock); // archived BEFORE any stock is added — mirrors the real user flow
        await catalogDb.Products.AddAsync(product);
        await catalogDb.SaveChangesAsync();

        _unitId = grams.Id.Value;
        _productId = product.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "plantry-lxm2: ListPantryAsync still resolves and renders an archived product's active stock")]
    public async Task ListPantry_Resolves_Archived_Product_With_Active_Stock()
    {
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(250m, _unitId, _locationId, _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        var pantry = await BuildQueryService().ListPantryAsync();

        var item = Assert.Single(pantry);
        Assert.Equal("Instant espresso", item.Name);
        Assert.Equal(250m, item.TotalQuantity);
        Assert.True(item.IsStocked);
        Assert.True(item.IsArchived);
    }

    [Fact(DisplayName = "plantry-lxm2: CountInStockAsync agrees with ListPantryAsync for a real archived-with-stock product")]
    public async Task CountInStock_Agrees_With_ListPantry_For_Archived_Product()
    {
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, _productId, Clock);
            stock.AddStock(250m, _unitId, _locationId, _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        var service = BuildQueryService();
        var pantry = await service.ListPantryAsync();
        var count = await service.CountInStockAsync();

        Assert.Equal(pantry.Count, count);
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "plantry-lxm2: a genuinely unknown product (never in the catalog) is still skipped, not rendered as \"?\"")]
    public async Task ListPantry_Still_Skips_A_Product_Absent_From_The_Catalog_Entirely()
    {
        var orphanProductId = Guid.CreateVersion7();
        await using (var invDb = NewInventoryDb())
        {
            var stock = InventoryProductStock.Start(_household, orphanProductId, Clock);
            stock.AddStock(10m, _unitId, _locationId, _userId, Clock);
            await invDb.ProductStocks.AddAsync(stock);
            await invDb.SaveChangesAsync();
        }

        var pantry = await BuildQueryService().ListPantryAsync();

        Assert.Empty(pantry);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private InventoryQueryService BuildQueryService()
    {
        var catalogDb = NewCatalogDb();
        var productRepo = new Plantry.Catalog.Infrastructure.ProductRepository(catalogDb);
        var unitRepo = new Plantry.Catalog.Infrastructure.UnitRepository(catalogDb);
        var categoryRepo = new CatalogCategoryRepository(catalogDb);
        var locationRepo = new CatalogLocationRepository(catalogDb);
        var catalog = new CatalogReadFacade(productRepo, unitRepo, categoryRepo, locationRepo);
        var conversions = new CatalogConversionProvider(productRepo, unitRepo);
        var stocks = new ProductStockRepository(NewInventoryDb());
        var tenant = new TestTenant(_household.Value);

        return new InventoryQueryService(stocks, catalog, conversions, new FixedHorizon(), Clock, tenant);
    }

    private InventoryDbContext NewInventoryDb()
    {
        var ctx = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }

    private sealed class FixedHorizon : IExpiringSoonHorizon
    {
        public Task<int> GetDaysAsync(CancellationToken ct = default) =>
            Task.FromResult(HouseholdInventorySettings.DefaultExpiringSoonDays);
    }
}
