using Plantry.Pantry.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1 unit tests for <see cref="LowStockRule"/> (plantry-oh27.1): the threshold-positivity invariant
/// (a rule always holds a strictly positive threshold — "no rule" is the absence of a row) and
/// <see cref="LowStockRule.IsRunningLow"/>'s boundary at equality.
/// </summary>
public sealed class LowStockRuleTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();

    // ── Create / SetThreshold validation ───────────────────────────────────

    [Fact(DisplayName = "Create rejects a zero threshold — absence of a row is 'no threshold', not zero")]
    public void Create_Rejects_Zero_Threshold()
    {
        var clock = new MutableClock();

        Assert.Throws<ArgumentOutOfRangeException>(() => LowStockRule.Create(Household, Product, 0m, clock));
    }

    [Fact(DisplayName = "Create rejects a negative threshold")]
    public void Create_Rejects_Negative_Threshold()
    {
        var clock = new MutableClock();

        Assert.Throws<ArgumentOutOfRangeException>(() => LowStockRule.Create(Household, Product, -1m, clock));
    }

    [Fact]
    public void Create_Persists_Threshold_HouseholdId_And_ProductId()
    {
        var clock = new MutableClock();

        var rule = LowStockRule.Create(Household, Product, 3.5m, clock);

        Assert.Equal(Household, rule.HouseholdId);
        Assert.Equal(Product, rule.ProductId);
        Assert.Equal(3.5m, rule.Threshold);
    }

    [Fact(DisplayName = "SetThreshold rejects a zero or negative value")]
    public void SetThreshold_Rejects_NonPositive_Value()
    {
        var clock = new MutableClock();
        var rule = LowStockRule.Create(Household, Product, 5m, clock);

        Assert.Throws<ArgumentOutOfRangeException>(() => rule.SetThreshold(0m, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.SetThreshold(-1m, clock));
    }

    [Fact]
    public void SetThreshold_Updates_Value_And_Bumps_UpdatedAt()
    {
        var clock = new MutableClock();
        var rule = LowStockRule.Create(Household, Product, 5m, clock);
        var createdAt = rule.UpdatedAt;

        clock.Advance(TimeSpan.FromHours(1));
        rule.SetThreshold(8m, clock);

        Assert.Equal(8m, rule.Threshold);
        Assert.Equal(clock.UtcNow, rule.UpdatedAt);
        Assert.True(rule.UpdatedAt > createdAt);
    }

    // ── IsRunningLow boundary ────────────────────────────────────────────────

    [Fact(DisplayName = "IsRunningLow is true when onHand equals the threshold")]
    public void IsRunningLow_True_When_OnHand_Equals_Threshold()
    {
        var rule = LowStockRule.Create(Household, Product, 5m, new MutableClock());

        Assert.True(rule.IsRunningLow(5m));
    }

    [Fact(DisplayName = "IsRunningLow is true when onHand is less than the threshold")]
    public void IsRunningLow_True_When_OnHand_Below_Threshold()
    {
        var rule = LowStockRule.Create(Household, Product, 10m, new MutableClock());

        Assert.True(rule.IsRunningLow(3m));
        Assert.True(rule.IsRunningLow(0m));
    }

    [Fact(DisplayName = "IsRunningLow is false when onHand exceeds the threshold")]
    public void IsRunningLow_False_When_OnHand_Above_Threshold()
    {
        var rule = LowStockRule.Create(Household, Product, 5m, new MutableClock());

        Assert.False(rule.IsRunningLow(5.001m));
        Assert.False(rule.IsRunningLow(100m));
    }
}
