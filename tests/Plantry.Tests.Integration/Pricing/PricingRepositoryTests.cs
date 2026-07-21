using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Pricing.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.SharedKernel;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Pricing;

/// <summary>
/// L3 integration tests proving <see cref="PriceObservation"/> round-trips through EF against a
/// real Postgres schema, the latest-price read model returns the most recent row, and RLS prevents
/// household B from reading household A's observations.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PricingRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
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

    [Fact(DisplayName = "PriceObservation round-trips all fields through EF")]
    public async Task PriceObservation_RoundTrips_All_Fields()
    {
        var observedAt = DateTimeOffset.UtcNow;

        await using (var ctx = NewPricingDb())
        {
            var obs = PriceObservation.Record(
                _household, _productId, null,
                price: 3.99m, quantity: 500m, unitId: _unitId,
                unitPrice: 0.00798m,
                source: PriceSource.Purchase,
                merchantText: "Superstore",
                sourceRef: _sourceRef,
                observedAt: observedAt,
                userId: _userId);
            await ctx.PriceObservations.AddAsync(obs);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var loaded = await ctx2.PriceObservations.SingleAsync(p => p.ProductId == _productId);

        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal(_productId, loaded.ProductId);
        Assert.Null(loaded.SkuId);
        Assert.Equal(3.99m, loaded.Price);
        Assert.Equal(500m, loaded.Quantity);
        Assert.Equal(_unitId, loaded.UnitId);
        Assert.Equal(0.00798m, loaded.UnitPrice);
        Assert.Equal(PriceSource.Purchase, loaded.Source);
        Assert.Equal("Superstore", loaded.MerchantText);
        Assert.Equal(_sourceRef, loaded.SourceRef);
        Assert.Equal(_userId, loaded.UserId);
    }

    [Fact(DisplayName = "LatestForProduct returns the most recent observation by observed_at")]
    public async Task LatestForProduct_Returns_Most_Recent()
    {
        var earlier = DateTimeOffset.UtcNow.AddDays(-2);
        var later = DateTimeOffset.UtcNow.AddDays(-1);

        await using (var ctx = NewPricingDb())
        {
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 2m, 1m, _unitId, 2m,
                PriceSource.Purchase, null, _sourceRef, earlier, _userId));
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 3m, 1m, _unitId, 3m,
                PriceSource.Purchase, null, _sourceRef, later, _userId));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var latest = await repo.LatestForProductAsync(_productId);

        Assert.NotNull(latest);
        Assert.Equal(3m, latest.Price);
    }

    [Fact(DisplayName = "LatestForSku returns the most recent observation by observed_at for that SKU")]
    public async Task LatestForSku_Returns_Most_Recent_For_Sku()
    {
        var skuId = Guid.CreateVersion7();
        var earlier = DateTimeOffset.UtcNow.AddDays(-3);
        var later = DateTimeOffset.UtcNow.AddDays(-1);

        await using (var ctx = NewPricingDb())
        {
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, skuId, 1.50m, 1m, _unitId, null,
                PriceSource.Purchase, null, _sourceRef, earlier, _userId));
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, skuId, 1.80m, 1m, _unitId, null,
                PriceSource.Purchase, null, _sourceRef, later, _userId));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var latest = await repo.LatestForSkuAsync(skuId);

        Assert.NotNull(latest);
        Assert.Equal(1.80m, latest.Price);
    }

    [Fact(DisplayName = "Deal observation round-trips validity window + store_id through EF")]
    public async Task Deal_Observation_RoundTrips_Window_And_Store()
    {
        var storeId = Guid.CreateVersion7();
        var from = new DateOnly(2026, 7, 1);
        var to = new DateOnly(2026, 7, 7);

        await using (var ctx = NewPricingDb())
        {
            var obs = PriceObservation.Record(
                _household, _productId, null, 2.50m, 1m, _unitId, 2.50m,
                PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
                validFrom: from, validTo: to, storeId: storeId);
            await ctx.PriceObservations.AddAsync(obs);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var loaded = await ctx2.PriceObservations.SingleAsync(p => p.ProductId == _productId);

        Assert.Equal(PriceSource.Deal, loaded.Source);
        Assert.Equal(from, loaded.ValidFrom);
        Assert.Equal(to, loaded.ValidTo);
        Assert.Equal(storeId, loaded.StoreId);
    }

    [Fact(DisplayName = "CheapestActiveDeal returns MIN(unit_price) among in-window deals against the supplied clock")]
    public async Task CheapestActiveDeal_Returns_Min_UnitPrice_In_Window()
    {
        var today = new DateOnly(2026, 7, 4);

        await using (var ctx = NewPricingDb())
        {
            // Active, cheapest.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 2m, 1m, _unitId, 2m,
                PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
                validFrom: new(2026, 7, 1), validTo: new(2026, 7, 7)));
            // Active, dearer.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 3m, 1m, _unitId, 3m,
                PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
                validFrom: new(2026, 7, 2), validTo: new(2026, 7, 6)));
            // Cheaper but expired — must be excluded.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 1m, 1m, _unitId, 1m,
                PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
                validFrom: new(2026, 6, 1), validTo: new(2026, 6, 7)));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var cheapest = await repo.CheapestActiveDealForProductAsync(_productId, today);

        Assert.NotNull(cheapest);
        Assert.Equal(2m, cheapest.UnitPrice);
    }

    [Fact(DisplayName = "CheapestActiveDeal returns null when no deal is active for the supplied clock")]
    public async Task CheapestActiveDeal_Returns_Null_When_None_Active()
    {
        var today = new DateOnly(2026, 7, 4);

        await using (var ctx = NewPricingDb())
        {
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 1m, 1m, _unitId, 1m,
                PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
                validFrom: new(2026, 6, 1), validTo: new(2026, 6, 7)));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        Assert.Null(await repo.CheapestActiveDealForProductAsync(_productId, today));
    }

    [Fact(DisplayName = "LatestForProduct is source-filtered — a deal row never contaminates a purchase-cost query")]
    public async Task LatestForProduct_Excludes_Deal_Rows()
    {
        await using (var ctx = NewPricingDb())
        {
            // Older purchase.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 4m, 1m, _unitId, 4m,
                PriceSource.Purchase, null, _sourceRef, DateTimeOffset.UtcNow.AddDays(-2), _userId));
            // Newer DEAL row — would win a naive "latest" query, must be excluded.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 2m, 1m, _unitId, 2m,
                PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
                validFrom: new(2026, 7, 1), validTo: new(2026, 7, 7)));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var latest = await repo.LatestForProductAsync(_productId);

        Assert.NotNull(latest);
        Assert.Equal(PriceSource.Purchase, latest.Source);
        Assert.Equal(4m, latest.Price);
    }

    [Fact(DisplayName = "LatestForProduct includes a Manual observation, and the newest of Purchase/Manual wins")]
    public async Task LatestForProduct_Includes_Manual_Rows()
    {
        await using (var ctx = NewPricingDb())
        {
            // Older purchase.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 4m, 1m, _unitId, 4m,
                PriceSource.Purchase, "Superstore", _sourceRef, DateTimeOffset.UtcNow.AddDays(-2), _userId));
            // Newer MANUAL row — a household estimate, no source_ref — must win (plantry-3fqm).
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 3m, 1m, _unitId, 3m,
                PriceSource.Manual, null, null, DateTimeOffset.UtcNow.AddDays(-1), _userId));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var latest = await repo.LatestForProductAsync(_productId);

        Assert.NotNull(latest);
        Assert.Equal(PriceSource.Manual, latest.Source);
        Assert.Equal(3m, latest.Price);
        Assert.Null(latest.SourceRef);
    }

    [Fact(DisplayName = "LatestForSku includes a Manual observation, and the newest of Purchase/Manual wins")]
    public async Task LatestForSku_Includes_Manual_Rows()
    {
        var skuId = Guid.CreateVersion7();

        await using (var ctx = NewPricingDb())
        {
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, skuId, 4m, 1m, _unitId, 4m,
                PriceSource.Purchase, "Superstore", _sourceRef, DateTimeOffset.UtcNow.AddDays(-2), _userId));
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, skuId, 3m, 1m, _unitId, 3m,
                PriceSource.Manual, null, null, DateTimeOffset.UtcNow.AddDays(-1), _userId));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var latest = await repo.LatestForSkuAsync(skuId);

        Assert.NotNull(latest);
        Assert.Equal(PriceSource.Manual, latest.Source);
        Assert.Equal(3m, latest.Price);
    }

    [Fact(DisplayName = "ListPurchasesAwaitingStore stays Purchase-only — a Manual row is never eligible for the DM-16 sweep")]
    public async Task ListPurchasesAwaitingStore_Excludes_Manual_Rows()
    {
        await using (var ctx = NewPricingDb())
        {
            // A purchase with a merchant and no store — eligible for backfill.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 4m, 1m, _unitId, 4m,
                PriceSource.Purchase, "Superstore", _sourceRef, DateTimeOffset.UtcNow, _userId));
            // A Manual row has no merchant and no store — must never surface in the sweep even though
            // store_id is also null here.
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, null, 3m, 1m, _unitId, 3m,
                PriceSource.Manual, null, null, DateTimeOffset.UtcNow, _userId));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var awaiting = await repo.ListPurchasesAwaitingStoreAsync();

        var row = Assert.Single(awaiting);
        Assert.Equal(PriceSource.Purchase, row.Source);
        Assert.Equal(4m, row.Price);
    }

    [Fact(DisplayName = "A Manual observation round-trips a null source_ref through EF")]
    public async Task Manual_Observation_RoundTrips_Null_SourceRef()
    {
        await using (var ctx = NewPricingDb())
        {
            var obs = PriceObservation.Record(
                _household, _productId, null, 2.99m, 1m, _unitId, 2.99m,
                PriceSource.Manual, merchantText: null, sourceRef: null, DateTimeOffset.UtcNow, _userId);
            await ctx.PriceObservations.AddAsync(obs);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var loaded = await ctx2.PriceObservations.SingleAsync(p => p.ProductId == _productId);

        Assert.Equal(PriceSource.Manual, loaded.Source);
        Assert.Null(loaded.SourceRef);
        Assert.Null(loaded.MerchantText);
        Assert.Null(loaded.StoreId);
    }

    [Fact(DisplayName = "LatestForSku is source-filtered — a deal row never contaminates a purchase-cost query")]
    public async Task LatestForSku_Excludes_Deal_Rows()
    {
        var skuId = Guid.CreateVersion7();

        await using (var ctx = NewPricingDb())
        {
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, skuId, 4m, 1m, _unitId, 4m,
                PriceSource.Purchase, null, _sourceRef, DateTimeOffset.UtcNow.AddDays(-2), _userId));
            await ctx.PriceObservations.AddAsync(PriceObservation.Record(
                _household, _productId, skuId, 2m, 1m, _unitId, 2m,
                PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
                validFrom: new(2026, 7, 1), validTo: new(2026, 7, 7)));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewPricingDb();
        var repo = new PriceObservationRepository(ctx2);
        var latest = await repo.LatestForSkuAsync(skuId);

        Assert.NotNull(latest);
        Assert.Equal(PriceSource.Purchase, latest.Source);
        Assert.Equal(4m, latest.Price);
    }

    [Fact(DisplayName = "Check constraint rejects an inverted deal validity window (valid_from > valid_to)")]
    public async Task Inverted_Validity_Window_Is_Rejected_By_Check_Constraint()
    {
        await using var ctx = NewPricingDb();
        await ctx.PriceObservations.AddAsync(PriceObservation.Record(
            _household, _productId, null, 2m, 1m, _unitId, 2m,
            PriceSource.Deal, "Flyer", _sourceRef, DateTimeOffset.UtcNow, _userId,
            validFrom: new(2026, 7, 7), validTo: new(2026, 7, 1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact(DisplayName = "RLS: household B cannot read household A's price observations")]
    public async Task RLS_Household_B_Cannot_Read_Household_A_Observations()
    {
        var householdA = HouseholdId.New();
        var householdB = HouseholdId.New();

        await using (var ctxA = NewPricingDbFor(householdA))
        {
            await ctxA.PriceObservations.AddAsync(PriceObservation.Record(
                householdA, _productId, null, 2m, 1m, _unitId, null,
                PriceSource.Purchase, null, _sourceRef, DateTimeOffset.UtcNow, _userId));
            await ctxA.SaveChangesAsync();
        }

        await using var ctxB = NewPricingDbFor(householdB);
        var count = await ctxB.PriceObservations.CountAsync();
        Assert.Equal(0, count);
    }

    private DbContextOptions<PricingDbContext> PricingOptions() =>
        new DbContextOptionsBuilder<PricingDbContext>().UseNpgsql(db.ConnectionString).Options;

    private PricingDbContext NewPricingDb()
    {
        var ctx = new PricingDbContext(PricingOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private PricingDbContext NewPricingDbFor(HouseholdId household)
    {
        var ctx = new PricingDbContext(PricingOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
