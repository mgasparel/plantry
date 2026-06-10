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
        Task.FromResult(Items.Where(p => p.ProductId == productId).MaxBy(p => p.ObservedAt));

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.SkuId == skuId).MaxBy(p => p.ObservedAt));
}

internal sealed class FakeUnitPriceCalculator(decimal? returnValue) : IUnitPriceCalculator
{
    public Task<decimal?> TryNormalizeAsync(decimal price, decimal quantity, Guid unitId, CancellationToken ct = default) =>
        Task.FromResult(returnValue);
}
