using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests proving <see cref="LowStockRule"/> (plantry-oh27.1) persists correctly through
/// EF against a real Postgres schema, that the RLS policy on <c>low_stock_rule</c> isolates it across
/// households exactly like every other inventory.* table, and that <see cref="InventoryQueryService"/>
/// surfaces the rule correctly on the pantry list. Replaces the retired
/// <c>ProductStock.LowStockThreshold</c> coverage (<c>LowStockThresholdTests</c>).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class LowStockRuleTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Persistence ──────────────────────────────────────────────────────

    [Fact(DisplayName = "LowStockRule persists through EF round-trip when created")]
    public async Task LowStockRule_Persists_And_Reloads_Correctly()
    {
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var rule = LowStockRule.Create(_householdA, _productId, 3.5m, SystemClock.Instance);
            await ctx.LowStockRules.AddAsync(rule);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewInventoryDb(_householdA);
        var loaded = await ctx2.LowStockRules
            .SingleAsync(r => r.HouseholdId == _householdA && r.ProductId == _productId);

        Assert.Equal(3.5m, loaded.Threshold);
    }

    [Fact(DisplayName = "No row exists for a product with no threshold set — absence, not null/zero")]
    public async Task LowStockRule_Absent_When_Not_Set()
    {
        await using var ctx = NewInventoryDb(_householdA);
        var found = await ctx.LowStockRules
            .SingleOrDefaultAsync(r => r.HouseholdId == _householdA && r.ProductId == _productId);

        Assert.Null(found);
    }

    [Fact(DisplayName = "A rule can be created for a product with no ProductStock row at all (e.g. a parent product)")]
    public async Task LowStockRule_Persists_With_No_ProductStock_Row()
    {
        await using (var ctx = NewInventoryDb(_householdA))
        {
            // Deliberately no ProductStock.Start / AddAsync — the rule must not require one.
            var rule = LowStockRule.Create(_householdA, _productId, 5m, SystemClock.Instance);
            await ctx.LowStockRules.AddAsync(rule);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewInventoryDb(_householdA);
        var stockCount = await ctx2.ProductStocks.CountAsync(s => s.ProductId == _productId);
        var loaded = await ctx2.LowStockRules
            .SingleAsync(r => r.HouseholdId == _householdA && r.ProductId == _productId);

        Assert.Equal(0, stockCount);
        Assert.Equal(5m, loaded.Threshold);
    }

    [Fact(DisplayName = "IsRunningLow is correct from a persisted rule (onHand at threshold → true)")]
    public async Task IsRunningLow_Correct_After_Reload_When_OnHand_At_Threshold()
    {
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var stock = ProductStock.Start(_householdA, _productId, SystemClock.Instance);
            stock.AddStock(5m, _unitId, _locationId, _userId, SystemClock.Instance);
            await ctx.ProductStocks.AddAsync(stock);

            var rule = LowStockRule.Create(_householdA, _productId, 5m, SystemClock.Instance); // 5 ≤ 5 → running low
            await ctx.LowStockRules.AddAsync(rule);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewInventoryDb(_householdA);
        var stock2 = await ctx2.ProductStocks
            .Include(p => p.Entries)
            .SingleAsync(p => p.HouseholdId == _householdA && p.ProductId == _productId);
        var rule2 = await ctx2.LowStockRules
            .SingleAsync(r => r.HouseholdId == _householdA && r.ProductId == _productId);

        var onHand = stock2.ActiveLotsFefo().Sum(l => l.Quantity);
        Assert.True(rule2.IsRunningLow(onHand));
    }

    // ── EF query-filter cross-household isolation (see StockRlsIsolationTests for the Postgres-RLS-as-app_user proof) ─

    [Fact(DisplayName = "Household A cannot read household B's low stock rule (EF query filter)")]
    public async Task LowStockRule_CrossHousehold_Isolation_EfFilter()
    {
        var sharedProductId = Guid.CreateVersion7();

        await using (var ctxA = NewInventoryDb(_householdA))
        {
            await ctxA.LowStockRules.AddAsync(LowStockRule.Create(_householdA, sharedProductId, 10m, SystemClock.Instance));
            await ctxA.SaveChangesAsync();
        }

        await using (var ctxB = NewInventoryDb(_householdB))
        {
            await ctxB.LowStockRules.AddAsync(LowStockRule.Create(_householdB, sharedProductId, 99m, SystemClock.Instance));
            await ctxB.SaveChangesAsync();
        }

        await using var verifyA = NewInventoryDb(_householdA);
        var seenByA = await verifyA.LowStockRules.ToListAsync();
        Assert.All(seenByA, r => Assert.Equal(_householdA, r.HouseholdId));
        Assert.DoesNotContain(seenByA, r => r.Threshold == 99m);

        var ownA = Assert.Single(seenByA, r => r.ProductId == sharedProductId);
        Assert.Equal(10m, ownA.Threshold);
    }

    // ── Repository ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "LowStockRuleRepository.RemoveAsync deletes the row — clearing is absence, not null/zero")]
    public async Task Repository_RemoveAsync_Deletes_The_Rule()
    {
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var repo = new LowStockRuleRepository(ctx);
            await repo.AddAsync(LowStockRule.Create(_householdA, _productId, 4m, SystemClock.Instance));
            await repo.SaveChangesAsync();
        }

        await using (var ctx = NewInventoryDb(_householdA))
        {
            var repo = new LowStockRuleRepository(ctx);
            var rule = await repo.FindAsync(_householdA, _productId);
            Assert.NotNull(rule);
            await repo.RemoveAsync(rule!);
            await repo.SaveChangesAsync();
        }

        await using var verify = NewInventoryDb(_householdA);
        var found = await verify.LowStockRules.SingleOrDefaultAsync(r => r.ProductId == _productId);
        Assert.Null(found);
    }

    [Fact(DisplayName = "LowStockRuleRepository.ListForHouseholdAsync batch-loads every rule for the household, keyed by product id")]
    public async Task Repository_ListForHouseholdAsync_Returns_All_Rules_Keyed_By_Product()
    {
        var productB = Guid.CreateVersion7();
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var repo = new LowStockRuleRepository(ctx);
            await repo.AddAsync(LowStockRule.Create(_householdA, _productId, 4m, SystemClock.Instance));
            await repo.AddAsync(LowStockRule.Create(_householdA, productB, 8m, SystemClock.Instance));
            await repo.SaveChangesAsync();
        }

        await using var verify = NewInventoryDb(_householdA);
        var repo2 = new LowStockRuleRepository(verify);
        var rules = await repo2.ListForHouseholdAsync(_householdA);

        Assert.Equal(2, rules.Count);
        Assert.Equal(4m, rules[_productId].Threshold);
        Assert.Equal(8m, rules[productB].Threshold);
    }

    [Fact(DisplayName = "LowStockRuleRepository.TryAddAndSaveAsync returns false (not throw) when another request already inserted the same (household, product) rule")]
    public async Task Repository_TryAddAndSaveAsync_Returns_False_On_Duplicate_Key()
    {
        // Winner: inserts first via the plain AddAsync/SaveChangesAsync path.
        await using (var ctx = NewInventoryDb(_householdA))
        {
            var repo = new LowStockRuleRepository(ctx);
            await repo.AddAsync(LowStockRule.Create(_householdA, _productId, 4m, SystemClock.Instance));
            await repo.SaveChangesAsync();
        }

        // Loser: a second, independent request racing to create the same (household, product) rule.
        await using var ctx2 = NewInventoryDb(_householdA);
        var repo2 = new LowStockRuleRepository(ctx2);
        var inserted = await repo2.TryAddAndSaveAsync(LowStockRule.Create(_householdA, _productId, 9m, SystemClock.Instance));
        Assert.False(inserted);

        // The caller's documented recovery — re-read the winner from the same context — must work,
        // proving ChangeTracker.Clear() left the context usable after the duplicate-key catch.
        var winner = await repo2.FindAsync(_householdA, _productId);
        Assert.NotNull(winner);
        Assert.Equal(4m, winner!.Threshold);

        // Exactly one row exists for the pair — the failed insert did not land.
        await using var verify = NewInventoryDb(_householdA);
        Assert.Equal(1, await verify.LowStockRules.CountAsync(r => r.ProductId == _productId));
    }

    // ── Query service returns correct IsRunningLow from a persisted rule ─

    [Fact(DisplayName = "InventoryQueryService.ListPantry returns correct IsRunningLow from a persisted LowStockRule, "
        + "and leaves a stocked product with no rule at null/false — pins the batch rule-to-product join both ways")]
    public async Task QueryService_ListPantry_Surfaces_Correct_IsRunningLow_From_Persisted_Rule()
    {
        var grams = await SeedUnitAsync(_householdA);
        var withRule = await SeedProductAsync(_householdA, "Flour", grams);
        var withoutRule = await SeedProductAsync(_householdA, "Sugar", grams);

        await using (var ctx = NewInventoryDb(_householdA))
        {
            var stockWithRule = ProductStock.Start(_householdA, withRule, SystemClock.Instance);
            stockWithRule.AddStock(3m, grams, _locationId, _userId, SystemClock.Instance);
            await ctx.ProductStocks.AddAsync(stockWithRule);

            var stockWithoutRule = ProductStock.Start(_householdA, withoutRule, SystemClock.Instance);
            stockWithoutRule.AddStock(10m, grams, _locationId, _userId, SystemClock.Instance);
            await ctx.ProductStocks.AddAsync(stockWithoutRule);

            await ctx.LowStockRules.AddAsync(LowStockRule.Create(_householdA, withRule, 5m, SystemClock.Instance));
            await ctx.SaveChangesAsync();
        }

        var pantry = await BuildQueryService(_householdA).ListPantryAsync();

        var flour = Assert.Single(pantry, p => p.ProductId == withRule);
        Assert.Equal(5m, flour.LowStockThreshold);
        Assert.True(flour.IsRunningLow); // 3 ≤ 5 → true

        var sugar = Assert.Single(pantry, p => p.ProductId == withoutRule);
        Assert.Null(sugar.LowStockThreshold);
        Assert.False(sugar.IsRunningLow);
    }

    private async Task<Guid> SeedUnitAsync(HouseholdId household)
    {
        await using var ctx = NewInventoryDb(household);
        var unit = Unit.Create(household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await ctx.Units.AddAsync(unit);
        await ctx.SaveChangesAsync();
        return unit.Id.Value;
    }

    private async Task<Guid> SeedProductAsync(HouseholdId household, string name, Guid defaultUnitId)
    {
        await using var ctx = NewInventoryDb(household);
        var product = Product.Create(household, name, UnitId.From(defaultUnitId), SystemClock.Instance);
        await ctx.Products.AddAsync(product);
        await ctx.SaveChangesAsync();
        return product.Id.Value;
    }

    private InventoryQueryService BuildQueryService(HouseholdId household)
    {
        var catalogDb = NewInventoryDb(household);
        var productRepo = new ProductRepository(catalogDb);
        var unitRepo = new UnitRepository(catalogDb);
        var categoryRepo = new CategoryRepository(catalogDb);
        var locationRepo = new LocationRepository(catalogDb);
        var catalog = new CatalogReadFacade(productRepo, new UnitCodesAccessor(unitRepo), categoryRepo, locationRepo, new FakeHouseholdExpiryDefaultsReader());
        var conversions = new CatalogConversionProvider(productRepo, unitRepo);
        var stocks = new ProductStockRepository(NewInventoryDb(household));
        var rules = new LowStockRuleRepository(NewInventoryDb(household));
        var tenant = new TestTenant(household.Value);

        return new InventoryQueryService(stocks, rules, catalog, conversions, new FixedHorizon(), SystemClock.Instance, tenant);
    }

    private DbContextOptions<PantryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private PantryDbContext NewInventoryDb(HouseholdId household)
    {
        var ctx = new PantryDbContext(InventoryOptions());
        ctx.SetHouseholdId(household.Value);
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
