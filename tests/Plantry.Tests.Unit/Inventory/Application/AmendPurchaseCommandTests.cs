using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// Application-layer tests for the Inventory leg of purchase-entry amendment (ADR-023) — the
/// row-lock/transaction/save wiring around <see cref="ProductStock.AmendPurchase"/>, mirroring
/// <see cref="ConsumeStockCommandTests"/>'s house style.
/// </summary>
public sealed class AmendPurchaseCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    private FakeProductStockRepository StocksWithPurchasedLot(decimal quantity, out StockEntryId entryId)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        var lot = stock.AddStock(quantity, _unitId, _locationId, _userId, Clock);
        entryId = lot.Id;
        stocks.Items.Add(stock);
        return stocks;
    }

    private AmendPurchaseCommand Command(
        FakeProductStockRepository stocks, Guid entryId, Guid? household,
        decimal correctedQuantity = 3m, Guid? importLineId = null) =>
        new(_productId, entryId, correctedQuantity, importLineId ?? Guid.NewGuid(), _userId,
            stocks, Clock, new FakeTenantContext(household));

    [Fact]
    public async Task Amends_The_Lot_And_Saves_Inside_A_Transaction()
    {
        var stocks = StocksWithPurchasedLot(1m, out var entryId);

        var result = await Command(stocks, entryId.Value, _household, correctedQuantity: 3m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2m, result.Value);
        Assert.Equal(3m, stocks.Items.Single().Entries.Single().Quantity);
        Assert.Equal(1, stocks.SaveChangesCalls);
        Assert.Equal(1, stocks.TransactionScopes);
    }

    [Fact]
    public async Task Writes_An_Amendment_Journal_Row_With_Intake_Provenance()
    {
        var stocks = StocksWithPurchasedLot(1m, out var entryId);
        var importLineId = Guid.NewGuid();

        await Command(stocks, entryId.Value, _household, correctedQuantity: 3m, importLineId: importLineId).ExecuteAsync();

        var journal = Assert.Single(stocks.Items.Single().Journal, j => j.Reason == StockReason.Amendment);
        Assert.Equal(StockSourceType.Intake, journal.SourceType);
        Assert.Equal(importLineId, journal.SourceRef);
        Assert.Equal(+2m, journal.Delta);
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var stocks = StocksWithPurchasedLot(1m, out var entryId);

        var result = await Command(stocks, entryId.Value, household: null).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Fails_When_The_Product_Has_No_Stock()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, Guid.NewGuid(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.NoStock", result.Error.Code);
        Assert.Equal(1, stocks.TransactionScopes); // we entered the scope, then found nothing
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Propagates_The_Domain_Guard_Error_And_Does_Not_Save()
    {
        var stocks = StocksWithPurchasedLot(3m, out var entryId);

        var result = await Command(stocks, entryId.Value, _household, correctedQuantity: -1m).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidAmendQuantity", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task A_ZeroDelta_ReDrive_Succeeds_As_A_NoOp()
    {
        var stocks = StocksWithPurchasedLot(1m, out var entryId);
        var importLineId = Guid.NewGuid();
        await Command(stocks, entryId.Value, _household, correctedQuantity: 3m, importLineId: importLineId).ExecuteAsync();

        var result = await Command(stocks, entryId.Value, _household, correctedQuantity: 3m, importLineId: importLineId).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value);
        Assert.Equal(3m, stocks.Items.Single().Entries.Single().Quantity);
    }
}
