using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 pin for the CatalogReadFacade N+1 fix (plantry-hw39, absorbing plantry-rsy1): before
/// <see cref="HouseholdExpiryDefaultsAccessor"/> existed, <c>CatalogReadFacade.FindProductAsync</c>
/// resolved the household's freeze/thaw defaults via <see cref="IHouseholdExpiryDefaultsReader"/> on
/// every call, and <c>InventoryStockReaderAdapter</c> already calls <c>FindProductAsync</c> in a
/// per-product loop — so a recipe/meal-plan fulfilment read multiplied <c>households</c> reads by
/// product count. This test reproduces that exact shape (N <c>FindProductAsync</c> calls against a real
/// Postgres <c>identity.households</c> table) and asserts exactly ONE <c>households</c> query is issued
/// for the whole batch, against a real EF-backed <see cref="HouseholdExpiryDefaultsService"/> — not a
/// test double standing in for it — mirroring <c>MealPlanCatalogProductReaderAdapterTests</c>'s
/// per-instance/per-scope memoisation pattern.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class HouseholdExpiryDefaultsAccessorQueryCountTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly List<ProductId> _productIds = [];

    public async Task InitializeAsync()
    {
        await db.ResetAsync();

        await using var identityDb = new PlantryIdentityDbContext(IdentityOptions());
        var household = Household.Create("Household hw39", SystemClock.Instance);
        // Distinguishable from both the aggregate's baked-in default (90/3) and the no-tenant fallback
        // (also 90/3) — so a passing query-count assertion is exercising a real read, not a coincidence.
        household.SetDefaultDueDaysAfterFreezing(45);
        household.SetDefaultDueDaysAfterThawing(6);
        await identityDb.Households.AddAsync(household);
        await identityDb.SaveChangesAsync();
        _household = household.Id;

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

    [Fact(DisplayName = "FindProductAsync for 3 products through the SAME accessor issues exactly ONE households query (N+1 fix)")]
    public async Task FindProductAsync_ThreeProducts_IssuesOneHouseholdsQuery()
    {
        var counter = new QueryCountingInterceptor();
        await using var identityDb = new PlantryIdentityDbContext(IdentityOptions(counter));
        identityDb.SetHouseholdId(_household.Value);
        await using var catalogDb = NewCatalogDb();

        var facade = NewCatalogReadFacade(identityDb, catalogDb);

        // Mirrors InventoryStockReaderAdapter's per-product FindProductAsync loop (the amplification
        // point named in the original finding) — all three calls share the one request-scoped facade.
        foreach (var productId in _productIds)
        {
            var info = await facade.FindProductAsync(productId.Value);
            Assert.NotNull(info);
        }

        Assert.Equal(1, counter.CountMatching("households"));
    }

    [Fact(DisplayName = "A new request scope (new accessor instance) re-resolves — the cache is per-request, not static/shared")]
    public async Task NewScope_ReResolves_HouseholdsQuery()
    {
        var counter = new QueryCountingInterceptor();

        await using (var identityDb1 = new PlantryIdentityDbContext(IdentityOptions(counter)))
        await using (var catalogDb1 = NewCatalogDb())
        {
            identityDb1.SetHouseholdId(_household.Value);
            var facade1 = NewCatalogReadFacade(identityDb1, catalogDb1);
            await facade1.FindProductAsync(_productIds[0].Value);
            Assert.Equal(1, counter.CountMatching("households"));
        }

        await using (var identityDb2 = new PlantryIdentityDbContext(IdentityOptions(counter)))
        await using (var catalogDb2 = NewCatalogDb())
        {
            identityDb2.SetHouseholdId(_household.Value);
            var facade2 = NewCatalogReadFacade(identityDb2, catalogDb2);
            await facade2.FindProductAsync(_productIds[1].Value);
            Assert.Equal(2, counter.CountMatching("households"));
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private CatalogReadFacade NewCatalogReadFacade(PlantryIdentityDbContext identityDb, CatalogDbContext catalogDb)
    {
        var tenant = new FixedTenantContext(_household.Value);
        var expiryService = new HouseholdExpiryDefaultsService(
            new HouseholdRepository(identityDb), tenant, NullLogger<HouseholdExpiryDefaultsService>.Instance);
        var accessor = new HouseholdExpiryDefaultsAccessor(expiryService);
        var readerAdapter = new HouseholdExpiryDefaultsReaderAdapter(accessor);

        return new CatalogReadFacade(
            new ProductRepository(catalogDb), new UnitCodesAccessor(new UnitRepository(catalogDb)),
            new CategoryRepository(catalogDb), new LocationRepository(catalogDb), readerAdapter);
    }

    private DbContextOptions<PlantryIdentityDbContext> IdentityOptions(QueryCountingInterceptor? counter = null)
    {
        var builder = new DbContextOptionsBuilder<PlantryIdentityDbContext>().UseNpgsql(db.ConnectionString);
        if (counter is not null) builder.AddInterceptors(counter);
        return builder.Options;
    }

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class FixedTenantContext(Guid householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }
}
