using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.Tests.Unit.Pricing.Application;
using Plantry.Web.Deals;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="RecordDealObservationAdapter"/> (plantry-riqy) — the Deals→Pricing ACL adapter
/// that wraps <see cref="RecordObservationCommand"/> (already exhaustively covered by
/// <c>RecordObservationCommandTests</c>). Here we only pin the adapter's own mapping: null
/// quantity/unitId default to (1, empty-unit), a missing reviewer maps to <see cref="Guid.Empty"/>, and a
/// command failure is re-thrown as <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class RecordDealObservationAdapterTests
{
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly NullLogger<RecordObservationCommand> NullLogger = NullLogger<RecordObservationCommand>.Instance;
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);

    private static RecordDealObservationAdapter Adapter(
        FakePriceObservationRepository repo, FakeUnitPriceCalculator calculator, Guid? household) =>
        new(repo, calculator, new FakeTenantContext(household), NullLogger);

    [Fact(DisplayName = "RecordObservationAsync records a Deal-sourced observation with the validity window and store id")]
    public async Task Records_Deal_Observation_With_Window_And_Store()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(1.25m);
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var storeId = Guid.CreateVersion7();
        var dealId = Guid.CreateVersion7();
        var reviewerId = Guid.CreateVersion7();
        var from = new DateOnly(2026, 7, 1);
        var to = new DateOnly(2026, 7, 7);

        var id = await Adapter(repo, calculator, Household).RecordObservationAsync(
            productId, 2.50m, 2m, unitId, storeId, from, to, dealId, reviewerId, ObservedAt);

        Assert.NotEqual(Guid.Empty, id);
        var saved = Assert.Single(repo.Items);
        Assert.Equal(PriceSource.Deal, saved.Source);
        Assert.Equal(from, saved.ValidFrom);
        Assert.Equal(to, saved.ValidTo);
        Assert.Equal(storeId, saved.StoreId);
        Assert.Equal(dealId, saved.SourceRef);
        Assert.Equal(reviewerId, saved.UserId);
        Assert.Equal(ObservedAt, saved.ObservedAt);
    }

    [Fact(DisplayName = "RecordObservationAsync maps a null quantity/unitId to (1, Guid.Empty)")]
    public async Task Maps_Null_Quantity_And_Unit_To_Defaults()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(null);

        await Adapter(repo, calculator, Household).RecordObservationAsync(
            Guid.CreateVersion7(), 2.50m, quantity: null, unitId: null, Guid.CreateVersion7(),
            new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7), Guid.CreateVersion7(), null, ObservedAt);

        var saved = Assert.Single(repo.Items);
        Assert.Equal(1m, saved.Quantity);
        Assert.Equal(Guid.Empty, saved.UnitId);
    }

    [Fact(DisplayName = "RecordObservationAsync maps a null reviewer to Guid.Empty")]
    public async Task Maps_Null_Reviewer_To_Empty_Guid()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(1m);

        await Adapter(repo, calculator, Household).RecordObservationAsync(
            Guid.CreateVersion7(), 2.50m, 1m, Guid.CreateVersion7(), Guid.CreateVersion7(),
            new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7), Guid.CreateVersion7(), reviewedByUserId: null, ObservedAt);

        var saved = Assert.Single(repo.Items);
        Assert.Equal(Guid.Empty, saved.UserId);
    }

    [Fact(DisplayName = "RecordObservationAsync throws InvalidOperationException when the command fails (no household)")]
    public async Task Throws_On_Command_Failure()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(1m);
        var adapter = Adapter(repo, calculator, household: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.RecordObservationAsync(
            Guid.CreateVersion7(), 2.50m, 1m, Guid.CreateVersion7(), Guid.CreateVersion7(),
            new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7), Guid.CreateVersion7(), Guid.CreateVersion7(), ObservedAt));

        Assert.Contains("Record deal observation failed", ex.Message);
        Assert.Empty(repo.Items);
    }
}
