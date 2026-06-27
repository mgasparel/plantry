using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1 unit tests for the LowStockThreshold invariants on <see cref="ProductStock"/>:
/// null/zero threshold → IsRunningLow = false; onHand ≤ threshold → true; onHand > threshold → false.
/// </summary>
public sealed class ProductStockLowStockThresholdTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly Guid Location = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static (ProductStock Stock, MutableClock Clock) StockWithClock()
    {
        var clock = new MutableClock();
        return (ProductStock.Start(Household, Product, clock), clock);
    }

    private static ProductStock Stock()
    {
        var (stock, _) = StockWithClock();
        return stock;
    }

    // ── null / zero threshold ──────────────────────────────────────────────

    [Fact(DisplayName = "IsRunningLow is false when no threshold is set (null)")]
    public void IsRunningLow_False_When_Threshold_Is_Null()
    {
        var stock = Stock();
        // LowStockThreshold is null by default

        Assert.False(stock.IsRunningLow(0m));
        Assert.False(stock.IsRunningLow(10m));
    }

    [Fact(DisplayName = "IsRunningLow is false when threshold is explicitly set to zero")]
    public void IsRunningLow_False_When_Threshold_Is_Zero()
    {
        var (stock, clock) = StockWithClock();
        stock.SetLowStockThreshold(0m, clock);

        Assert.False(stock.IsRunningLow(0m));
        Assert.False(stock.IsRunningLow(100m));
    }

    [Fact(DisplayName = "IsRunningLow is false when threshold is cleared (set to null)")]
    public void IsRunningLow_False_When_Threshold_Cleared()
    {
        var (stock, clock) = StockWithClock();
        stock.SetLowStockThreshold(5m, clock); // set first
        stock.SetLowStockThreshold(null, clock);  // then clear

        Assert.False(stock.IsRunningLow(1m));
    }

    // ── onHand ≤ threshold → running low ──────────────────────────────────

    [Fact(DisplayName = "IsRunningLow is true when onHand equals the threshold")]
    public void IsRunningLow_True_When_OnHand_Equals_Threshold()
    {
        var (stock, clock) = StockWithClock();
        stock.SetLowStockThreshold(5m, clock);

        Assert.True(stock.IsRunningLow(5m));
    }

    [Fact(DisplayName = "IsRunningLow is true when onHand is less than the threshold")]
    public void IsRunningLow_True_When_OnHand_Below_Threshold()
    {
        var (stock, clock) = StockWithClock();
        stock.SetLowStockThreshold(10m, clock);

        Assert.True(stock.IsRunningLow(3m));
        Assert.True(stock.IsRunningLow(0m));
    }

    // ── onHand > threshold → not running low ──────────────────────────────

    [Fact(DisplayName = "IsRunningLow is false when onHand exceeds the threshold")]
    public void IsRunningLow_False_When_OnHand_Above_Threshold()
    {
        var (stock, clock) = StockWithClock();
        stock.SetLowStockThreshold(5m, clock);

        Assert.False(stock.IsRunningLow(5.001m));
        Assert.False(stock.IsRunningLow(100m));
    }

    // ── SetLowStockThreshold validation ───────────────────────────────────

    [Fact(DisplayName = "SetLowStockThreshold stores the value on LowStockThreshold")]
    public void SetLowStockThreshold_Persists_Value()
    {
        var (stock, clock) = StockWithClock();
        stock.SetLowStockThreshold(3.5m, clock);

        Assert.Equal(3.5m, stock.LowStockThreshold);
    }

    [Fact(DisplayName = "SetLowStockThreshold rejects a negative threshold")]
    public void SetLowStockThreshold_Rejects_Negative_Value()
    {
        var (stock, clock) = StockWithClock();

        Assert.Throws<ArgumentOutOfRangeException>(() => stock.SetLowStockThreshold(-1m, clock));
    }

    [Fact(DisplayName = "LowStockThreshold is null by default (no threshold configured)")]
    public void LowStockThreshold_Null_By_Default()
    {
        var stock = Stock();

        Assert.Null(stock.LowStockThreshold);
    }

    // ── UpdatedAt bump ────────────────────────────────────────────────────

    [Fact(DisplayName = "SetLowStockThreshold bumps UpdatedAt to clock.UtcNow")]
    public void SetLowStockThreshold_Bumps_UpdatedAt()
    {
        var (stock, clock) = StockWithClock();
        var createdAt = stock.CreatedAt;

        clock.Advance(TimeSpan.FromHours(1));
        stock.SetLowStockThreshold(5m, clock);

        Assert.Equal(clock.UtcNow, stock.UpdatedAt);
        Assert.True(stock.UpdatedAt > createdAt);
    }
}
