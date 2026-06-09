using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

public sealed class ConsumeStockCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    private FakeProductStockRepository StocksWithLot(decimal quantity, Guid? unitId = null)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        stock.AddStock(quantity, unitId ?? _unitId, _locationId, _userId, Clock);
        stocks.Items.Add(stock);
        return stocks;
    }

    private ConsumeStockCommand Command(
        FakeProductStockRepository stocks, IQuantityConverter converter, Guid? household,
        decimal amount = 30m, Guid? unitId = null, StockReason reason = StockReason.Consumed) =>
        new(_productId, amount, unitId ?? _unitId, reason, _userId, null, null,
            stocks, new FakeConversionProvider(converter), Clock, new FakeTenantContext(household));

    [Fact]
    public async Task Consumes_Across_The_Lot_And_Saves_Inside_A_Transaction()
    {
        var stocks = StocksWithLot(100m);

        var result = await Command(stocks, new IdentityQuantityConverter(), _household, amount: 30m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.HasShortfall);
        Assert.Equal(30m, result.Value.Deductions.Sum(d => d.Amount));
        Assert.Equal(70m, stocks.Items.Single().Entries.Single().Quantity);
        Assert.Equal(1, stocks.SaveChangesCalls);
        Assert.Equal(1, stocks.TransactionScopes);
    }

    [Fact]
    public async Task Reports_Shortfall_When_The_Pantry_Cannot_Satisfy_The_Request()
    {
        var stocks = StocksWithLot(20m);

        var result = await Command(stocks, new IdentityQuantityConverter(), _household, amount: 50m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.HasShortfall);
        Assert.Equal(30m, result.Value.ShortfallAmount);
        Assert.Equal(0m, stocks.Items.Single().Entries.Single().Quantity); // drained, not negative
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var stocks = StocksWithLot(100m);

        var result = await Command(stocks, new IdentityQuantityConverter(), household: null).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Fails_When_Reason_Is_A_Purchase()
    {
        var stocks = StocksWithLot(100m);

        var result = await Command(stocks, new IdentityQuantityConverter(), _household, reason: StockReason.Purchase).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidConsumeReason", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Fails_When_The_Product_Has_No_Stock()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, new IdentityQuantityConverter(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.NoStock", result.Error.Code);
        Assert.Equal(1, stocks.TransactionScopes); // we entered the scope, then found nothing
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Discards_Lot_With_Discarded_Reason()
    {
        var stocks = StocksWithLot(100m);

        var result = await Command(stocks, new IdentityQuantityConverter(), _household, amount: 100m, reason: StockReason.Discarded).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.HasShortfall);
        Assert.Equal(100m, result.Value.Deductions.Sum(d => d.Amount));
        Assert.Equal(0m, stocks.Items.Single().Entries.Single().Quantity);
    }

    [Fact]
    public async Task Fails_When_Amount_Is_Zero()
    {
        // Zero amount was the root cause of the silent-discard bug: the Discard URL omitted amount
        // so the handler received amount=0, the command failed, and the result was thrown away.
        var stocks = StocksWithLot(100m);

        var result = await Command(stocks, new IdentityQuantityConverter(), _household, amount: 0m).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidConsumeAmount", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Fails_Loudly_And_Does_Not_Save_When_Conversion_Is_Unresolvable()
    {
        var stocks = StocksWithLot(100m, unitId: _unitId);
        var requestUnit = Guid.CreateVersion7(); // no factor configured to/from the lot unit
        var converter = new FactorQuantityConverter([]);

        var result = await Command(stocks, converter, _household, amount: 10m, unitId: requestUnit).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Test.Unresolvable", result.Error.Code);
        Assert.Equal(100m, stocks.Items.Single().Entries.Single().Quantity); // untouched
        Assert.Equal(0, stocks.SaveChangesCalls);
    }
}
