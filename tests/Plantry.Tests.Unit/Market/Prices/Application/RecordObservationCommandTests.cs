using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Unit.Market;

namespace Plantry.Tests.Unit.Market.Prices.Application;

public sealed class RecordObservationCommandTests
{
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SourceRef = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly NullLogger<RecordObservationCommand> NullLogger =
        NullLogger<RecordObservationCommand>.Instance;

    private RecordObservationCommand Command(
        FakePriceObservationRepository repo,
        FakeUnitPriceCalculator calculator,
        Guid? householdId = null,
        decimal price = 3.99m,
        decimal quantity = 500m) =>
        new(ProductId, null, price, quantity, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(householdId ?? Household), NullLogger);

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
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(null), NullLogger)
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
            PriceSource.Deal, repo, calculator, new FakeTenantContext(Household), NullLogger,
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
    public async Task Manual_Source_Records_With_Null_SourceRef_And_No_Merchant()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(0.5m);

        var result = await new RecordObservationCommand(
            ProductId, null, 5m, 2m, UnitId, merchantText: null, sourceRef: null, Now, UserId,
            PriceSource.Manual, repo, calculator, new FakeTenantContext(Household), NullLogger)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(repo.Items);
        Assert.Equal(PriceSource.Manual, saved.Source);
        Assert.Null(saved.SourceRef);
        Assert.Null(saved.MerchantText);
        Assert.Null(saved.StoreId);
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

    // ── Intake-time deal-hit detection (plantry-j9q4) ────────────────────────────

    private static PriceObservation SeedDeal(
        FakePriceObservationRepository repo,
        Guid storeId,
        decimal dealUnitPrice,
        DateOnly from,
        DateOnly to,
        Guid? dealId = null)
    {
        var deal = PriceObservation.Record(
            HouseholdId.From(Household), ProductId, null,
            price: dealUnitPrice, quantity: 1m, unitId: UnitId,
            unitPrice: dealUnitPrice, source: PriceSource.Deal,
            merchantText: null, sourceRef: dealId ?? Guid.CreateVersion7(), observedAt: Now, userId: UserId,
            validFrom: from, validTo: to, storeId: storeId);
        repo.Items.Add(deal);
        return deal;
    }

    [Fact]
    public async Task Purchase_At_Deal_Price_At_The_Same_Store_In_Window_Records_The_Match()
    {
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var dealId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, storeId, dealUnitPrice: 0.00798m, today.AddDays(-1), today.AddDays(1), dealId);

        var calculator = new FakeUnitPriceCalculator(0.00798m);
        var result = await new RecordObservationCommand(
            ProductId, null, 3.99m, 500m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Equal(dealId, purchase.MatchedDealId);
    }

    [Fact]
    public async Task Purchase_At_The_Dearer_Of_Two_Active_Deals_Matches_That_Deal_Not_The_Cheapest()
    {
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var cheaperDealId = Guid.CreateVersion7();
        var dearerDealId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        // Two pack sizes of the same catalog product on sale at the same store/window (routine: one
        // flyer line per pack size resolved to the same product). The purchase is made at the dearer
        // deal's price — it must match that qualifying deal, not be rejected because the cheapest
        // active deal (which it never claimed) is below it.
        SeedDeal(repo, storeId, dealUnitPrice: 1.00m, today, today, cheaperDealId);
        SeedDeal(repo, storeId, dealUnitPrice: 2.00m, today, today, dearerDealId);

        var calculator = new FakeUnitPriceCalculator(2.00m);
        var result = await new RecordObservationCommand(
            ProductId, null, 2.00m, 1m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Equal(dearerDealId, purchase.MatchedDealId);
    }

    [Fact]
    public async Task Purchase_Within_Tolerance_Above_Deal_Price_Still_Records_The_Match()
    {
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var dealId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, storeId, dealUnitPrice: 1.00m, today, today, dealId);

        // 1.005 rounds to a unit price only 0.005 above the deal — within the 0.01 tolerance.
        var calculator = new FakeUnitPriceCalculator(1.005m);
        var result = await new RecordObservationCommand(
            ProductId, null, 1.005m, 1m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Equal(dealId, purchase.MatchedDealId);
    }

    [Fact]
    public async Task Purchase_Priced_Beyond_Tolerance_Above_Deal_Price_Is_Not_Matched()
    {
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, storeId, dealUnitPrice: 1.00m, today, today);

        var calculator = new FakeUnitPriceCalculator(1.50m);
        var result = await new RecordObservationCommand(
            ProductId, null, 1.50m, 1m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Null(purchase.MatchedDealId);
    }

    [Fact]
    public async Task Purchase_Priced_Beyond_Tolerance_Above_A_Tiny_PerBaseUnit_Deal_Price_Is_Not_Matched()
    {
        // Regression: the tolerance must be relative to the deal's unit price, not a flat cent amount — a
        // flat $0.01 allowance on a $0.00798/g deal would accept a purchase over 2x the deal price.
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, storeId, dealUnitPrice: 0.00798m, today, today);

        var calculator = new FakeUnitPriceCalculator(0.015m);
        var result = await new RecordObservationCommand(
            ProductId, null, 3.99m, 500m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Null(purchase.MatchedDealId);
    }

    [Fact]
    public async Task No_Active_Deal_For_The_Product_Leaves_MatchedDealId_Null()
    {
        var repo = new FakePriceObservationRepository();
        var calculator = new FakeUnitPriceCalculator(0.00798m);

        var result = await Command(repo, calculator).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(repo.Items);
        Assert.Null(saved.MatchedDealId);
    }

    [Fact]
    public async Task Deal_Whose_Window_Has_Lapsed_Is_Not_Matched()
    {
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, storeId, dealUnitPrice: 0.00798m, today.AddDays(-10), today.AddDays(-2));

        var calculator = new FakeUnitPriceCalculator(0.00798m);
        var result = await new RecordObservationCommand(
            ProductId, null, 3.99m, 500m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Null(purchase.MatchedDealId);
    }

    [Fact]
    public async Task Deal_With_Null_UnitPrice_Never_Shadows_A_Costable_Deal_At_The_Same_Store_And_Window()
    {
        // Regression: nulls-last ordering (fakes and production alike) — a DM-17 pack-size-less deal
        // (Price cheaper, UnitPrice null) must never win over a costable deal that qualifies for the
        // purchase's unit price, even though a naive nulls-first sort would pick it first.
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var costableDealId = Guid.CreateVersion7();

        // Pack-size-less deal: cheaper nominal Price, but null UnitPrice — must be ignored here.
        var unitlessDeal = PriceObservation.Record(
            HouseholdId.From(Household), ProductId, null,
            price: 1m, quantity: 1m, unitId: Guid.Empty,
            unitPrice: null, source: PriceSource.Deal,
            merchantText: null, sourceRef: Guid.CreateVersion7(), observedAt: Now, userId: UserId,
            validFrom: today, validTo: today, storeId: storeId);
        repo.Items.Add(unitlessDeal);
        SeedDeal(repo, storeId, dealUnitPrice: 0.00798m, today, today, costableDealId);

        var calculator = new FakeUnitPriceCalculator(0.00798m);
        var result = await new RecordObservationCommand(
            ProductId, null, 3.99m, 500m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Equal(costableDealId, purchase.MatchedDealId);
    }

    [Fact]
    public async Task Deal_At_A_Different_Store_Is_Not_Matched()
    {
        var repo = new FakePriceObservationRepository();
        var dealStoreId = Guid.CreateVersion7();
        var purchaseStoreId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, dealStoreId, dealUnitPrice: 0.00798m, today, today);

        var calculator = new FakeUnitPriceCalculator(0.00798m);
        var result = await new RecordObservationCommand(
            ProductId, null, 3.99m, 500m, UnitId, "Different Store", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: purchaseStoreId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Null(purchase.MatchedDealId);
    }

    [Fact]
    public async Task Purchase_With_Unresolved_Store_Is_Never_Matched_Even_When_A_Deal_Is_Active()
    {
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        // Seeded at a store, but the purchase below carries no resolved store (blank-merchant receipt line).
        SeedDeal(repo, storeId, dealUnitPrice: 0.00798m, today, today);

        var calculator = new FakeUnitPriceCalculator(0.00798m);
        var result = await new RecordObservationCommand(
            ProductId, null, 3.99m, 500m, UnitId, merchantText: null, SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: null)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Null(purchase.MatchedDealId);
    }

    [Fact]
    public async Task Purchase_Whose_UnitPrice_SoftFailed_Is_Never_Matched()
    {
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, storeId, dealUnitPrice: 0.00798m, today, today);

        var calculator = new FakeUnitPriceCalculator(null); // normalization soft-fail
        var result = await new RecordObservationCommand(
            ProductId, null, 3.99m, 500m, UnitId, "Superstore", SourceRef, Now, UserId,
            PriceSource.Purchase, repo, calculator, new FakeTenantContext(Household), NullLogger, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var purchase = repo.Items.Single(p => p.Source == PriceSource.Purchase);
        Assert.Null(purchase.MatchedDealId);
    }

    [Fact]
    public async Task Deal_Source_Observation_Is_Never_Itself_Matched_Against_A_Deal()
    {
        // Guards against a regression where a second ConfirmDeal-written observation could
        // erroneously match against another deal row purely by product+store+window overlap.
        var repo = new FakePriceObservationRepository();
        var storeId = Guid.CreateVersion7();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        SeedDeal(repo, storeId, dealUnitPrice: 2.50m, today, today);

        var calculator = new FakeUnitPriceCalculator(2.50m);
        var result = await new RecordObservationCommand(
            ProductId, null, 2.50m, 1m, UnitId, "Flyer", Guid.CreateVersion7(), Now, UserId,
            PriceSource.Deal, repo, calculator, new FakeTenantContext(Household), NullLogger,
            validFrom: today, validTo: today, storeId: storeId)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var newDealObservation = repo.Items.Single(p => p.Price == 2.50m && p.SkuId == null && p.UnitId == UnitId
            && p.Source == PriceSource.Deal && p.ValidFrom == today && p.MerchantText == "Flyer");
        Assert.Null(newDealObservation.MatchedDealId);
    }
}

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}
