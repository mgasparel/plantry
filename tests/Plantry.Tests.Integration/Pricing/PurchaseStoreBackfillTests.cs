using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Pricing.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Pricing;
using Xunit;

namespace Plantry.Tests.Integration.Pricing;

/// <summary>
/// L3 tests for the DM-16 store-id backfill unit of work (<see cref="PurchaseStoreBackfill"/>) wired over
/// the REAL cross-context seams (Pricing observations + Catalog's <see cref="Plantry.Catalog.Application.EnsureStoreByNameCommand"/>
/// via <see cref="StoreRepository"/>) against a real Postgres schema. Covers the four eligibility/idempotency
/// cases the sweep must get right: resolve-to-existing-store, create-new-store, blank-merchant-skipped, and
/// re-run-no-op.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PurchaseStoreBackfillTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero));
    private HouseholdId _household;
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _sourceRef = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Backfill resolves a purchase to an EXISTING store without minting a duplicate")]
    public async Task Backfill_Resolves_To_Existing_Store()
    {
        StoreId existingId;
        await using (var seedCatalog = NewCatalogDb())
        {
            var store = Store.Create(_household, "Metro", Clock);
            await seedCatalog.Stores.AddAsync(store);
            await seedCatalog.SaveChangesAsync();
            existingId = store.Id;
        }
        var observationId = await SeedPurchaseAsync(merchantText: "Metro");

        var resolved = await RunBackfillAsync();

        Assert.Equal(1, resolved);

        await using var verifyPricing = NewPricingDb();
        var observation = await verifyPricing.PriceObservations.SingleAsync(p => p.Id == observationId);
        Assert.Equal(existingId.Value, observation.StoreId);

        // No duplicate store minted — the existing "Metro" row is reused.
        await using var verifyCatalog = NewCatalogDb();
        var remaining = Assert.Single(await verifyCatalog.Stores.ToListAsync());
        Assert.Equal(existingId, remaining.Id);
    }

    [Fact(DisplayName = "Backfill mints a new manual store when the merchant has none, and stamps its id")]
    public async Task Backfill_Creates_New_Store_When_None_Exists()
    {
        var observationId = await SeedPurchaseAsync(merchantText: "Costco");

        var resolved = await RunBackfillAsync();

        Assert.Equal(1, resolved);

        await using var verifyCatalog = NewCatalogDb();
        var store = Assert.Single(await verifyCatalog.Stores.ToListAsync());
        Assert.Equal("Costco", store.Name);
        Assert.Null(store.ExternalRef); // purchase-side manual store, not a Flipp subscription

        await using var verifyPricing = NewPricingDb();
        var observation = await verifyPricing.PriceObservations.SingleAsync(p => p.Id == observationId);
        Assert.Equal(store.Id.Value, observation.StoreId);
    }

    [Fact(DisplayName = "Backfill leaves blank-merchant purchases null and mints no store for them")]
    public async Task Backfill_Skips_Blank_Merchant_Rows()
    {
        var nullMerchantId = await SeedPurchaseAsync(merchantText: null);
        var whitespaceMerchantId = await SeedPurchaseAsync(merchantText: "   ");
        var namedId = await SeedPurchaseAsync(merchantText: "Walmart");

        var resolved = await RunBackfillAsync();

        // Only the named purchase resolves; the two blank ones are ineligible.
        Assert.Equal(1, resolved);

        await using var verifyPricing = NewPricingDb();
        Assert.Null((await verifyPricing.PriceObservations.SingleAsync(p => p.Id == nullMerchantId)).StoreId);
        Assert.Null((await verifyPricing.PriceObservations.SingleAsync(p => p.Id == whitespaceMerchantId)).StoreId);
        Assert.NotNull((await verifyPricing.PriceObservations.SingleAsync(p => p.Id == namedId)).StoreId);

        // The blank rows minted no store — only "Walmart" exists.
        await using var verifyCatalog = NewCatalogDb();
        var store = Assert.Single(await verifyCatalog.Stores.ToListAsync());
        Assert.Equal("Walmart", store.Name);
    }

    [Fact(DisplayName = "Backfill is idempotent — a second run resolves nothing and mints no new store")]
    public async Task Backfill_Is_Idempotent_On_ReRun()
    {
        var observationId = await SeedPurchaseAsync(merchantText: "Loblaws");

        var firstRun = await RunBackfillAsync();
        Assert.Equal(1, firstRun);

        Guid? storeIdAfterFirstRun;
        await using (var afterFirst = NewPricingDb())
            storeIdAfterFirstRun = (await afterFirst.PriceObservations.SingleAsync(p => p.Id == observationId)).StoreId;
        Assert.NotNull(storeIdAfterFirstRun);

        // Second run: eligibility already excludes the resolved row → no-op.
        var secondRun = await RunBackfillAsync();
        Assert.Equal(0, secondRun);

        await using var verifyPricing = NewPricingDb();
        var observation = await verifyPricing.PriceObservations.SingleAsync(p => p.Id == observationId);
        Assert.Equal(storeIdAfterFirstRun, observation.StoreId); // unchanged

        await using var verifyCatalog = NewCatalogDb();
        Assert.Single(await verifyCatalog.Stores.ToListAsync()); // no duplicate store from the second sweep
    }

    /// <summary>Runs the backfill for the test household over a fresh pair of armed contexts.</summary>
    private async Task<int> RunBackfillAsync()
    {
        await using var catalogDb = NewCatalogDb();
        await using var pricingDb = NewPricingDb();
        var backfill = new PurchaseStoreBackfill(
            new PriceObservationRepository(pricingDb),
            new StoreRepository(catalogDb),
            new TestTenant(_household.Value),
            Clock,
            NullLogger<PurchaseStoreBackfill>.Instance);
        return await backfill.RunAsync();
    }

    private async Task<PriceObservationId> SeedPurchaseAsync(string? merchantText)
    {
        await using var pricingDb = NewPricingDb();
        var observation = PriceObservation.Record(
            _household, _productId, null,
            price: 3.99m, quantity: 500m, unitId: _unitId, unitPrice: 0.00798m,
            source: PriceSource.Purchase, merchantText: merchantText,
            sourceRef: _sourceRef, observedAt: Clock.UtcNow, userId: _userId);
        await pricingDb.PriceObservations.AddAsync(observation);
        await pricingDb.SaveChangesAsync();
        return observation.Id;
    }

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private PricingDbContext NewPricingDb()
    {
        var ctx = new PricingDbContext(
            new DbContextOptionsBuilder<PricingDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
