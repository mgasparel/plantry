using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Pricing.Application;

public sealed class RecordAmendedObservationCommandTests
{
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SourceRef = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly NullLogger<RecordAmendedObservationCommand> NullLogger =
        NullLogger<RecordAmendedObservationCommand>.Instance;

    private static PriceObservation SeedPurchase(FakePriceObservationRepository repo) =>
        Seed(repo, PriceObservation.Record(
            HouseholdId.From(Household), ProductId, null,
            price: 3.98m, quantity: 1m, unitId: UnitId,
            unitPrice: 3.98m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId));

    private static PriceObservation Seed(FakePriceObservationRepository repo, PriceObservation obs)
    {
        repo.Items.Add(obs);
        return obs;
    }

    [Fact]
    public async Task Produces_The_Amending_Row_And_Supersedes_The_Original()
    {
        var repo = new FakePriceObservationRepository();
        var original = SeedPurchase(repo);
        var calculator = new FakeUnitPriceCalculator(1.3267m);

        var result = await new RecordAmendedObservationCommand(
            original.Id, correctedQuantity: 3m, UserId, repo, calculator, new FakeTenantContext(Household), NullLogger)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, repo.Items.Count);

        var amendment = repo.Items.Single(o => o.Id == result.Value);
        Assert.Equal(3m, amendment.Quantity);
        Assert.Equal(1.3267m, amendment.UnitPrice);
        Assert.Equal(original.Price, amendment.Price);
        Assert.Equal(original.ObservedAt, amendment.ObservedAt);
        Assert.Equal(original.SourceRef, amendment.SourceRef);
        Assert.Equal(original.Id, amendment.AmendsId);

        Assert.Equal(amendment.Id, original.SupersededById);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Second_Amendment_Chains_Off_The_First_Amendments_Live_Row()
    {
        var repo = new FakePriceObservationRepository();
        var purchase = SeedPurchase(repo);

        var first = await new RecordAmendedObservationCommand(
            purchase.Id, 3m, UserId, repo, new FakeUnitPriceCalculator(1.33m), new FakeTenantContext(Household), NullLogger)
            .ExecuteAsync();
        Assert.True(first.IsSuccess);

        var second = await new RecordAmendedObservationCommand(
            first.Value, 2.5m, UserId, repo, new FakeUnitPriceCalculator(1.59m), new FakeTenantContext(Household), NullLogger)
            .ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Equal(3, repo.Items.Count);

        var firstAmendment = repo.Items.Single(o => o.Id == first.Value);
        var secondAmendment = repo.Items.Single(o => o.Id == second.Value);

        Assert.Equal(secondAmendment.Id, firstAmendment.SupersededById);
        Assert.Null(secondAmendment.SupersededById); // the new live row
        Assert.Equal(firstAmendment.Id, secondAmendment.AmendsId);
        Assert.Equal(2.5m, secondAmendment.Quantity);
    }

    [Fact]
    public async Task Amending_An_Already_Superseded_Row_Throws_Rather_Than_Forking()
    {
        var repo = new FakePriceObservationRepository();
        var purchase = SeedPurchase(repo);

        var first = await new RecordAmendedObservationCommand(
            purchase.Id, 3m, UserId, repo, new FakeUnitPriceCalculator(1.33m), new FakeTenantContext(Household), NullLogger)
            .ExecuteAsync();
        Assert.True(first.IsSuccess);

        // Re-amending the now-dead original row (not the live amendment) must fail loudly, not silently fork.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecordAmendedObservationCommand(
                purchase.Id, 2m, UserId, repo, new FakeUnitPriceCalculator(1.99m), new FakeTenantContext(Household), NullLogger)
                .ExecuteAsync());
    }

    [Fact]
    public async Task Returns_NotFound_When_The_Original_Observation_Does_Not_Exist()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(1m);

        var result = await new RecordAmendedObservationCommand(
            PriceObservationId.New(), 3m, UserId, repo, calculator, new FakeTenantContext(Household), NullLogger)
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(RecordAmendedObservationCommand.OriginalNotFound, result.Error);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task Returns_Unauthorized_When_No_Household_Is_Armed()
    {
        var repo = new FakePriceObservationRepository();
        var original = SeedPurchase(repo);
        var calculator = new FakeUnitPriceCalculator(1.3267m);

        var result = await new RecordAmendedObservationCommand(
            original.Id, 3m, UserId, repo, calculator, new FakeTenantContext(householdId: null), NullLogger)
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
    }

    [Fact]
    public async Task Preserves_A_Null_UnitPrice_When_The_Corrected_Quantity_Still_Soft_Fails()
    {
        var repo = new FakePriceObservationRepository();
        var original = SeedPurchase(repo);
        var calculator = new FakeUnitPriceCalculator(null);

        var result = await new RecordAmendedObservationCommand(
            original.Id, 3m, UserId, repo, calculator, new FakeTenantContext(Household), NullLogger)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var amendment = repo.Items.Single(o => o.Id == result.Value);
        Assert.Null(amendment.UnitPrice);
    }
}
