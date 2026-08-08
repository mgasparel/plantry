using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.Pantry.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Inventory;
using Xunit;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 end-to-end proof for plantry-hh1f: a product with NO per-product freeze/thaw override (the exact
/// shape of an auto-created leftovers product, <c>CookRecipe.cs:214</c> — <c>categoryId: null</c>, no
/// <c>SetExpiryDefaults</c> call) still gets its expiry recomputed on freeze/thaw, because
/// <c>TransferStockCommand</c> now resolves <see cref="CatalogReadFacade"/> → Catalog's
/// <c>ExpiryDefaultResolver</c> → the real EF-backed household default through the full
/// Identity → Composition → Catalog anti-corruption chain (<see cref="HouseholdExpiryDefaultsService"/>,
/// <see cref="HouseholdExpiryDefaultsReaderAdapter"/>), not a test double standing in for it.
///
/// <para>Also proves the freeze→thaw→freeze cycle does not compound (SPECIFIC GUIDANCE): each
/// transition recomputes from "today", not from the lot's current expiry, so refreezing after a thaw
/// lands back on exactly <c>today + household default</c>, never <c>today + default + default</c>.</para>
///
/// <para>plantry-qckx adds one more proof: a household default changed through
/// <see cref="HouseholdExpiryDefaultsService.SetAllAsync"/> — the exact write path /Settings/Expiry
/// exercises (plantry-hw39) — flows through to the next freeze, satisfying that ticket's acceptance
/// criterion end-to-end against this file's already-real resolver chain.</para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TransferHouseholdExpiryDefaultsTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private Guid _productId;
    private Guid _unitId;
    private Guid _fridgeId;
    private Guid _freezerId;
    // Fixed, not SystemClock: the SUT (ProductStock.Transfer) recomputes DateOnly.FromDateTime(clock.
    // UtcNow.UtcDateTime) at execution time, so a static-init-time `DateTime.UtcNow` snapshot would flake
    // on any run straddling UTC midnight between type load and the transfer (Gate 10.A). Mirrors the
    // FixedClock pattern already used throughout this project (e.g. HouseholdInviteRlsTests.cs).
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly IClock Clock = new FixedClock(Now);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    // A household default distinguishable from both the Household aggregate's own baked-in default
    // (90/3) and HouseholdExpiryDefaultsService's no-tenant fallback (also 90/3) — so a passing
    // assertion proves the persisted row is actually being read, not just falling through to a
    // coincidentally-matching hardcoded default.
    private const int HouseholdAfterFreezing = 45;
    private const int HouseholdAfterThawing = 6;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();

        await using (var identityDb = new PlantryIdentityDbContext(IdentityOptions()))
        await using (var catalogDb = new PantryDbContext(CatalogOptions()))
        {
            // Household.Create mints its own id — capture it into _household once created, then use
            // that for every subsequent query filter (catalogDb.SetHouseholdId, etc.).
            var household = Household.Create("Household hh1f", Clock);
            await identityDb.Households.AddAsync(household);
            household.SetDefaultDueDaysAfterFreezing(HouseholdAfterFreezing);
            household.SetDefaultDueDaysAfterThawing(HouseholdAfterThawing);
            await identityDb.SaveChangesAsync();
            _household = household.Id;

            catalogDb.SetHouseholdId(_household.Value);
            var seeder = new CatalogReferenceDataSeeder(catalogDb);
            await seeder.SeedAsync(_household);

            var fridge = await catalogDb.Locations.SingleAsync(l => l.Name == "Fridge");
            var freezer = await catalogDb.Locations.SingleAsync(l => l.Name == "Freezer");
            var kg = await catalogDb.Units.SingleAsync(u => u.Code == "kg");
            _fridgeId = fridge.Id.Value;
            _freezerId = freezer.Id.Value;
            _unitId = kg.Id.Value;

            // The auto-created-leftovers shape (plantry-hh1f's original report): no category, no
            // SetExpiryDefaults call, so DefaultDueDaysAfterFreezing/Thawing are both null.
            var product = Product.Create(_household, "Leftover casserole", kg.Id, Clock);
            await catalogDb.Products.AddAsync(product);
            await catalogDb.SaveChangesAsync();
            _productId = product.Id.Value;
        }

        await using (var inventoryDb = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, _productId, Clock);
            stock.AddStock(2m, _unitId, _fridgeId, Guid.CreateVersion7(), Clock, expiryDate: Today.AddDays(5));
            await inventoryDb.ProductStocks.AddAsync(stock);
            await inventoryDb.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Freeze recomputes an override-less product's expiry to the household's after-freezing default")]
    public async Task Freeze_NoProductOverride_UsesHouseholdDefault()
    {
        var result = await Transfer(_fridgeId, _freezerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Freeze, result.Value.Kind);
        Assert.True(result.Value.DefaultApplied);
        Assert.Equal(Today.AddDays(HouseholdAfterFreezing), result.Value.ExpiryDate);
    }

    [Fact(DisplayName = "Freeze -> thaw -> freeze does not compound: each transition recomputes from today, landing on the same household default every time")]
    public async Task FreezeThenThawThenFreeze_DoesNotCompound()
    {
        var freeze1 = await Transfer(_fridgeId, _freezerId);
        Assert.True(freeze1.IsSuccess);
        Assert.Equal(Today.AddDays(HouseholdAfterFreezing), freeze1.Value.ExpiryDate);

        var thaw = await Transfer(_freezerId, _fridgeId);
        Assert.True(thaw.IsSuccess);
        Assert.Equal(TransferKind.Thaw, thaw.Value.Kind);
        Assert.Equal(Today.AddDays(HouseholdAfterThawing), thaw.Value.ExpiryDate);

        var freeze2 = await Transfer(_fridgeId, _freezerId);
        Assert.True(freeze2.IsSuccess);
        Assert.Equal(TransferKind.Freeze, freeze2.Value.Kind);
        // Not today + AfterFreezing + AfterFreezing, and not offset from the thaw's expiry — the
        // second freeze lands on exactly the same value as the first.
        Assert.Equal(Today.AddDays(HouseholdAfterFreezing), freeze2.Value.ExpiryDate);
        Assert.Equal(freeze1.Value.ExpiryDate, freeze2.Value.ExpiryDate);
    }

    [Fact(DisplayName = "Freeze picks up a household default changed through HouseholdExpiryDefaultsService.SetAllAsync (plantry-hw39 write path)")]
    public async Task Freeze_PicksUp_DefaultChangedThroughSettingsWritePath()
    {
        // Distinguishable from every other constant this file uses (90/3 baked-in, 45/6 seeded in
        // InitializeAsync) so a passing assertion proves the LIVE write, not a coincidental default.
        const int newAfterFreezing = 120;

        await using (var identityDb = new PlantryIdentityDbContext(IdentityOptions()))
        {
            identityDb.SetHouseholdId(_household.Value);
            var tenant = new FixedTenantContext(_household.Value);
            var writeService = new HouseholdExpiryDefaultsService(
                new HouseholdRepository(identityDb), tenant, NullLogger<HouseholdExpiryDefaultsService>.Instance);

            // The exact write path /Settings/Expiry (plantry-hw39) exercises through the page model.
            var setResult = await writeService.SetAllAsync(newAfterFreezing, HouseholdAfterThawing);
            Assert.True(setResult.IsSuccess);
        }

        var result = await Transfer(_fridgeId, _freezerId);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Freeze, result.Value.Kind);
        Assert.True(result.Value.DefaultApplied);
        Assert.Equal(Today.AddDays(newAfterFreezing), result.Value.ExpiryDate);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<Result<TransferOutcome>> Transfer(Guid fromLocationId, Guid toLocationId)
    {
        await using var catalogDb = NewCatalogDb();
        await using var identityDb = new PlantryIdentityDbContext(IdentityOptions());
        identityDb.SetHouseholdId(_household.Value);
        await using var inventoryDb = NewInventoryDb();

        var tenant = new FixedTenantContext(_household.Value);
        var expiryDefaults = new HouseholdExpiryDefaultsReaderAdapter(
            new HouseholdExpiryDefaultsAccessor(
                new HouseholdExpiryDefaultsService(
                    new HouseholdRepository(identityDb), tenant, NullLogger<HouseholdExpiryDefaultsService>.Instance)));
        var catalogFacade = new CatalogReadFacade(
            new ProductRepository(catalogDb), new UnitCodesAccessor(new UnitRepository(catalogDb)),
            new CategoryRepository(catalogDb), new LocationRepository(catalogDb), expiryDefaults);

        var stocks = new ProductStockRepository(inventoryDb);
        var stockBefore = await stocks.FindAsync(_household, _productId);
        var lotId = stockBefore!.Entries.Single(e => e.LocationId == fromLocationId).Id;

        var command = new TransferStockCommand(
            _productId, lotId.Value, toLocationId, 2m, stocks, catalogFacade, Clock, tenant);
        return await command.ExecuteAsync();
    }

    private DbContextOptions<PantryDbContext> CatalogOptions() =>
        new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private DbContextOptions<PlantryIdentityDbContext> IdentityOptions() =>
        new DbContextOptionsBuilder<PlantryIdentityDbContext>().UseNpgsql(db.ConnectionString).Options;

    private DbContextOptions<PantryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private PantryDbContext NewCatalogDb()
    {
        var ctx = new PantryDbContext(CatalogOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private PantryDbContext NewInventoryDb()
    {
        var ctx = new PantryDbContext(InventoryOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class FixedTenantContext(Guid householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
