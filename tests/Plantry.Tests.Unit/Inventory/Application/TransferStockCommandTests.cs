using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L1 unit tests for the "Move" action (plantry-6owm): <see cref="TransferStockCommand"/>. Covers the
/// household/no-stock/unknown-location guards plus the Catalog-facade wiring that resolves the
/// destination/source frozen-ness and the after-freezing/after-thawing defaults — the transition
/// math/guards themselves are covered at the domain level in <c>ProductStockTransferTests</c>.
/// </summary>
public sealed class TransferStockCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock Clock = new(Now);
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _fridge = Guid.CreateVersion7();
    private readonly Guid _freezer = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    private (FakeProductStockRepository Stocks, StockEntryId LotId) StocksWithLot(
        Guid locationId, decimal quantity, DateOnly? expiry)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        var lot = stock.AddStock(quantity, _unitId, locationId, _userId, Clock, expiryDate: expiry);
        stocks.Items.Add(stock);
        return (stocks, lot.Id);
    }

    private FakeCatalogReadFacade CatalogWith(int? freezeDays, int? thawDays)
    {
        var catalog = new FakeCatalogReadFacade
        {
            Products = { new CatalogProductInfo(
                _productId, "Chicken thighs", null, _unitId, "kg", CanHoldStock: true,
                DefaultDueDaysAfterFreezing: freezeDays, DefaultDueDaysAfterThawing: thawDays) },
        };
        catalog.LocationFrozenFlags[_fridge] = false;
        catalog.LocationFrozenFlags[_freezer] = true;
        return catalog;
    }

    [Fact]
    public async Task Transfer_FullLot_Freeze_ResolvesCatalogFacts_AndRecomputesExpiry()
    {
        var (stocks, lotId) = StocksWithLot(_fridge, 2m, expiry: DateOnly.FromDateTime(Now.UtcDateTime).AddDays(5));
        var catalog = CatalogWith(freezeDays: 90, thawDays: 2);

        var result = await new TransferStockCommand(
            _productId, lotId.Value, _freezer, 2m,
            stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferKind.Freeze, result.Value.Kind);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(90), result.Value.ExpiryDate);
        Assert.True(result.Value.DefaultApplied);
        var lot = stocks.Items.Single().Entries.Single();
        Assert.Equal(_freezer, lot.LocationId);
        Assert.NotNull(lot.FrozenAt);
        Assert.Equal(1, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Transfer_Partial_Splits_And_Saves()
    {
        var (stocks, lotId) = StocksWithLot(_fridge, 5m, expiry: DateOnly.FromDateTime(Now.UtcDateTime).AddDays(5));
        var catalog = CatalogWith(freezeDays: 90, thawDays: 2);

        var result = await new TransferStockCommand(
            _productId, lotId.Value, _freezer, 2m,
            stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.SplitEntryId);
        var stock = stocks.Items.Single();
        Assert.Equal(2, stock.Entries.Count);
        Assert.Equal(5m, stock.Entries.Sum(e => e.Quantity));
    }

    [Fact]
    public async Task Transfer_With_No_Catalog_Default_Moves_And_Sets_Timestamp_Leaves_Expiry()
    {
        var expiry = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(14);
        var (stocks, lotId) = StocksWithLot(_fridge, 1m, expiry);
        var catalog = CatalogWith(freezeDays: null, thawDays: null);

        var result = await new TransferStockCommand(
            _productId, lotId.Value, _freezer, 1m,
            stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.DefaultApplied);
        Assert.Equal(expiry, result.Value.ExpiryDate);
        Assert.NotNull(stocks.Items.Single().Entries.Single().FrozenAt);
    }

    [Fact]
    public async Task Transfer_Fails_When_Destination_Location_Unknown()
    {
        var (stocks, lotId) = StocksWithLot(_fridge, 1m, expiry: null);
        var catalog = CatalogWith(freezeDays: 90, thawDays: 2);
        var unknownLocation = Guid.CreateVersion7();

        var result = await new TransferStockCommand(
            _productId, lotId.Value, unknownLocation, 1m,
            stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.UnknownLocation", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Transfer_Fails_When_No_Household_In_Context()
    {
        var (stocks, lotId) = StocksWithLot(_fridge, 1m, expiry: null);
        var catalog = CatalogWith(freezeDays: 90, thawDays: 2);

        var result = await new TransferStockCommand(
            _productId, lotId.Value, _freezer, 1m,
            stocks, catalog, Clock, new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Transfer_Fails_When_No_Stock_Record()
    {
        var stocks = new FakeProductStockRepository();
        var catalog = CatalogWith(freezeDays: 90, thawDays: 2);

        var result = await new TransferStockCommand(
            _productId, Guid.NewGuid(), _freezer, 1m,
            stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.NoStock", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task Transfer_Rejects_SameLocation_Without_Saving()
    {
        var (stocks, lotId) = StocksWithLot(_fridge, 1m, expiry: null);
        var catalog = CatalogWith(freezeDays: 90, thawDays: 2);

        var result = await new TransferStockCommand(
            _productId, lotId.Value, _fridge, 1m,
            stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.SameLocation", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }
}
