using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L2 tests for <see cref="SaveCountsCommand"/> — the batch reconcile command (P4-4a / TS-6).
/// Covers partial-success vector semantics: one item's failure does not roll back committed items.
/// </summary>
public sealed class SaveCountsCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();

    private FakeProductStockRepository StocksWithLot(Guid productId, decimal quantity, Guid? locationId = null)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), productId, Clock);
        stock.AddStock(quantity, _unitId, locationId ?? _locationId, _userId, Clock);
        stocks.Items.Add(stock);
        return stocks;
    }

    private SaveCountsCommand Command(
        FakeProductStockRepository stocks,
        IReadOnlyList<CountItem> items,
        IQuantityConverter? converter = null,
        Guid? household = default,
        bool noHousehold = false) =>
        new(items, _userId, stocks,
            new FakeConversionProvider(converter ?? new IdentityQuantityConverter()),
            Clock, new FakeTenantContext(noHousehold ? null : (household ?? _household)));

    // ── Basic batch behaviour ─────────────────────────────────────────────────

    [Fact(DisplayName = "Returns a per-item result vector with one entry per input item")]
    public async Task Returns_Per_Item_Result_Vector()
    {
        var product1 = Guid.CreateVersion7();
        var product2 = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        // seed both products
        foreach (var p in new[] { product1, product2 })
        {
            var s = ProductStock.Start(HouseholdId.From(_household), p, Clock);
            s.AddStock(10m, _unitId, _locationId, _userId, Clock);
            stocks.Items.Add(s);
        }

        var items = new List<CountItem>
        {
            new(product1, _locationId, 12m, _unitId),
            new(product2, _locationId, 8m, _unitId),
        };

        var result = await Command(stocks, items).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.All(result.Value, r => Assert.True(r.IsSuccess));
        Assert.Equal(product1, result.Value[0].ProductId);
        Assert.Equal(product2, result.Value[1].ProductId);
    }

    [Fact(DisplayName = "Empty batch returns an empty success vector")]
    public async Task Empty_Batch_Returns_Empty_Vector()
    {
        var stocks = new FakeProductStockRepository();

        var result = await Command(stocks, []).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    // ── TS-6: partial-success — one failure does not roll back the rest ───────

    [Fact(DisplayName = "TS-6: one failing item does not roll back previously committed items")]
    public async Task TS6_One_Failing_Item_Does_Not_Roll_Back_Committed_Items()
    {
        var goodProduct = Guid.CreateVersion7();
        var badProduct = Guid.CreateVersion7(); // no stock seeded → will fail Consume direction

        var stocks = new FakeProductStockRepository();
        var goodStock = ProductStock.Start(HouseholdId.From(_household), goodProduct, Clock);
        goodStock.AddStock(10m, _unitId, _locationId, _userId, Clock);
        stocks.Items.Add(goodStock);
        // badProduct has no stock root at all; a downward count (count<0 not valid) —
        // instead use a negative counted value guard to trigger failure.
        // Actually to trigger failure: use negative counted value on a valid product.
        var badStock = ProductStock.Start(HouseholdId.From(_household), badProduct, Clock);
        badStock.AddStock(5m, _unitId, _locationId, _userId, Clock);
        stocks.Items.Add(badStock);

        var items = new List<CountItem>
        {
            new(goodProduct, _locationId, 15m, _unitId),   // Up by 5 — should succeed
            new(badProduct, _locationId, -1m, _unitId),    // Invalid negative → should fail
        };

        var result = await Command(stocks, items).ExecuteAsync();

        Assert.True(result.IsSuccess); // batch-level still succeeds
        Assert.Equal(2, result.Value.Count);

        var goodResult = result.Value[0];
        Assert.True(goodResult.IsSuccess);
        Assert.Equal(CountDirection.Up, goodResult.Outcome!.Direction);

        var badResult = result.Value[1];
        Assert.False(badResult.IsSuccess);
        Assert.NotNull(badResult.FailureReason);
        Assert.Equal("Inventory.InvalidCountedValue", badResult.FailureReason.Code);

        // Good product's stock was committed and is reflected in the in-memory store
        var savedGood = stocks.Items.Single(s => s.ProductId == goodProduct);
        Assert.Equal(2, savedGood.Entries.Count); // original + correction lot
    }

    [Fact(DisplayName = "TS-6: each item runs in its own independent transaction scope")]
    public async Task TS6_Each_Item_Runs_In_Its_Own_Transaction()
    {
        var product1 = Guid.CreateVersion7();
        var product2 = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        foreach (var p in new[] { product1, product2 })
        {
            var s = ProductStock.Start(HouseholdId.From(_household), p, Clock);
            s.AddStock(10m, _unitId, _locationId, _userId, Clock);
            stocks.Items.Add(s);
        }

        var items = new List<CountItem>
        {
            new(product1, _locationId, 10m, _unitId), // NoOp
            new(product2, _locationId, 12m, _unitId), // Up
        };

        await Command(stocks, items).ExecuteAsync();

        // One transaction scope per item
        Assert.Equal(2, stocks.TransactionScopes);
    }

    // ── Guard ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Fails when no household in context")]
    public async Task Fails_When_No_Household_In_Context()
    {
        var items = new List<CountItem>
        {
            new(Guid.CreateVersion7(), _locationId, 5m, _unitId),
        };

        var result = await Command(new FakeProductStockRepository(), items, noHousehold: true).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── TS-7 idempotency through SaveCounts ───────────────────────────────────

    [Fact(DisplayName = "TS-7: re-saving same counts is a per-item NoOp (partial batch resubmission)")]
    public async Task TS7_Resaving_Same_Counts_Is_NoOp_Per_Item()
    {
        var product = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), product, Clock);
        stock.AddStock(5m, _unitId, _locationId, _userId, Clock);
        stocks.Items.Add(stock);

        var items = new List<CountItem> { new(product, _locationId, 8m, _unitId) };

        // First save: Up by 3
        var first = await Command(stocks, items).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Equal(CountDirection.Up, first.Value.Single().Outcome!.Direction);

        // Second save: same countedValue=8, now recorded=8 → NoOp
        var second = await Command(stocks, items).ExecuteAsync();
        Assert.True(second.IsSuccess);
        Assert.Equal(CountDirection.NoOp, second.Value.Single().Outcome!.Direction);
    }
}
