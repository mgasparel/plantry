using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Pricing.Application;

public sealed class RecordObservationCommandTests
{
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SourceRef = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private RecordObservationCommand Command(
        FakePriceObservationRepository repo,
        FakeUnitPriceCalculator calculator,
        Guid? householdId = null,
        decimal price = 3.99m,
        decimal quantity = 500m) =>
        new(ProductId, null, price, quantity, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(householdId ?? Household));

    [Fact]
    public async Task Saves_Observation_With_Calculated_UnitPrice_On_Happy_Path()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(0.00798m);

        var result = await Command(repo, calculator).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(repo.Items);
        Assert.Equal(0.00798m, saved.UnitPrice);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Saves_Observation_With_Null_UnitPrice_When_Calculator_Returns_Null()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(null);

        var result = await Command(repo, calculator).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(repo.Items);
        Assert.Null(saved.UnitPrice);
    }

    [Fact]
    public async Task Returns_Failure_When_No_Household_In_Context()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(1m);

        var result = await new RecordObservationCommand(
            ProductId, null, 1m, 1m, UnitId, null, SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(null))
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task Returns_New_Id_On_Success()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(0.5m);

        var result = await Command(repo, calculator).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value.Value);
    }

    [Fact]
    public async Task Deal_Source_Records_Validity_Window_And_StoreId()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(2.50m);
        var storeId = Guid.CreateVersion7();
        var dealRef = Guid.CreateVersion7();
        var from = new DateOnly(2026, 7, 1);
        var to = new DateOnly(2026, 7, 7);

        var result = await new RecordObservationCommand(
            ProductId, null, 2.50m, 1m, UnitId, "Flyer", dealRef, Now, UserId,
            PriceSource.Deal, repo, calculator, new FakeTenantContext(Household),
            validFrom: from, validTo: to, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(repo.Items);
        Assert.Equal(PriceSource.Deal, saved.Source);
        Assert.Equal(from, saved.ValidFrom);
        Assert.Equal(to, saved.ValidTo);
        Assert.Equal(storeId, saved.StoreId);
        Assert.Equal(dealRef, saved.SourceRef);
        Assert.Equal(2.50m, saved.UnitPrice);
    }

    [Fact]
    public async Task Purchase_Source_Leaves_Window_And_StoreId_Null()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(0.00798m);

        var result = await Command(repo, calculator).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(repo.Items);
        Assert.Null(saved.ValidFrom);
        Assert.Null(saved.ValidTo);
        Assert.Null(saved.StoreId);
    }
}

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

internal sealed class FakePriceObservationRepository : IPriceObservationRepository
{
    public List<PriceObservation> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task AddAsync(PriceObservation observation, CancellationToken ct = default)
    {
        Items.Add(observation);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.ProductId == productId && p.Source == PriceSource.Purchase)
            .MaxBy(p => p.ObservedAt));

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.SkuId == skuId && p.Source == PriceSource.Purchase)
            .MaxBy(p => p.ObservedAt));

    public Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.ProductId == productId && p.Source == PriceSource.Deal
                && p.ValidFrom <= today && p.ValidTo >= today)
            .OrderBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefault());
}

internal sealed class FakeUnitPriceCalculator(decimal? returnValue) : IUnitPriceCalculator
{
    public Task<decimal?> TryNormalizeAsync(decimal price, decimal quantity, Guid unitId, CancellationToken ct = default) =>
        Task.FromResult(returnValue);
}
