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
