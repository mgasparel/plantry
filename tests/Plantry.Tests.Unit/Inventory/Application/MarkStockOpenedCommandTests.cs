using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L1 unit tests for the "Mark opened" / "Open" badge row actions (plantry-1le6):
/// <see cref="MarkStockOpenedCommand"/> and <see cref="UnmarkStockOpenedCommand"/>. Covers the
/// household/no-stock guards plus the Catalog-facade wiring that resolves the after-opening default —
/// the clamp/guard math itself is covered at the domain level in <c>ProductStockMarkOpenedTests</c>.
/// </summary>
public sealed class MarkStockOpenedCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock Clock = new(Now);
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    private (FakeProductStockRepository Stocks, StockEntryId LotId) StocksWithSealedLot(DateOnly? expiry)
    {
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), _productId, Clock);
        var lot = stock.AddStock(5m, _unitId, _locationId, _userId, Clock, expiryDate: expiry);
        stocks.Items.Add(stock);
        return (stocks, lot.Id);
    }

    private static FakeCatalogReadFacade CatalogWith(Guid productId, int? defaultDueDaysAfterOpening) => new()
    {
        Products = { new CatalogProductInfo(
            productId, "Mustard", null, Guid.NewGuid(), "ea", CanHoldStock: true,
            DefaultDueDaysAfterOpening: defaultDueDaysAfterOpening) },
    };

    [Fact]
    public async Task MarkOpened_Resolves_The_Catalog_Default_And_Recomputes_Expiry()
    {
        var (stocks, lotId) = StocksWithSealedLot(expiry: DateOnly.FromDateTime(Now.UtcDateTime).AddDays(90));
        var catalog = CatalogWith(_productId, defaultDueDaysAfterOpening: 5);

        var result = await new MarkStockOpenedCommand(
            _productId, lotId.Value, stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.DefaultApplied);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(5), result.Value.ExpiryDate);
        Assert.True(stocks.Items.Single().Entries.Single().IsOpen);
        Assert.Equal(1, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task MarkOpened_With_No_Catalog_Default_Flips_Flag_Leaves_Expiry()
    {
        var expiry = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(14);
        var (stocks, lotId) = StocksWithSealedLot(expiry);
        var catalog = CatalogWith(_productId, defaultDueDaysAfterOpening: null);

        var result = await new MarkStockOpenedCommand(
            _productId, lotId.Value, stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.DefaultApplied);
        Assert.Equal(expiry, result.Value.ExpiryDate);
    }

    [Fact]
    public async Task MarkOpened_Fails_When_No_Household_In_Context()
    {
        var (stocks, lotId) = StocksWithSealedLot(expiry: null);
        var catalog = CatalogWith(_productId, defaultDueDaysAfterOpening: 5);

        var result = await new MarkStockOpenedCommand(
            _productId, lotId.Value, stocks, catalog, Clock, new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task MarkOpened_Fails_When_No_Stock_Record()
    {
        var stocks = new FakeProductStockRepository();
        var catalog = CatalogWith(_productId, defaultDueDaysAfterOpening: 5);

        var result = await new MarkStockOpenedCommand(
            _productId, Guid.NewGuid(), stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.NoStock", result.Error.Code);
        Assert.Equal(0, stocks.SaveChangesCalls);
    }

    [Fact]
    public async Task MarkOpened_Rejects_An_AlreadyOpen_Lot_Without_Saving()
    {
        var (stocks, lotId) = StocksWithSealedLot(expiry: null);
        var catalog = CatalogWith(_productId, defaultDueDaysAfterOpening: 5);
        await new MarkStockOpenedCommand(_productId, lotId.Value, stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();
        var savesAfterFirstMark = stocks.SaveChangesCalls;

        var result = await new MarkStockOpenedCommand(
            _productId, lotId.Value, stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotAlreadyOpen", result.Error.Code);
        Assert.Equal(savesAfterFirstMark, stocks.SaveChangesCalls); // no additional save
    }

    [Fact]
    public async Task UnmarkOpened_Clears_The_Flag_Without_Restoring_Expiry()
    {
        var (stocks, lotId) = StocksWithSealedLot(expiry: DateOnly.FromDateTime(Now.UtcDateTime).AddDays(90));
        var catalog = CatalogWith(_productId, defaultDueDaysAfterOpening: 5);
        await new MarkStockOpenedCommand(_productId, lotId.Value, stocks, catalog, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        var result = await new UnmarkStockOpenedCommand(
            _productId, lotId.Value, stocks, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(stocks.Items.Single().Entries.Single().IsOpen);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(5), result.Value.ExpiryDate); // stays at the recomputed value
    }

    [Fact]
    public async Task UnmarkOpened_Fails_When_No_Household_In_Context()
    {
        var (stocks, lotId) = StocksWithSealedLot(expiry: null);

        var result = await new UnmarkStockOpenedCommand(
            _productId, lotId.Value, stocks, Clock, new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task UnmarkOpened_Fails_When_Lot_Is_Sealed()
    {
        var (stocks, lotId) = StocksWithSealedLot(expiry: null);

        var result = await new UnmarkStockOpenedCommand(
            _productId, lotId.Value, stocks, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotOpen", result.Error.Code);
    }
}
