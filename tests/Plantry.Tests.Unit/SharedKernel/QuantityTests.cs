using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.SharedKernel;

public sealed class QuantityTests
{
    private static readonly Guid UnitId = Guid.NewGuid();

    [Fact]
    public void Constructor_Stores_Amount_And_UnitId()
    {
        var qty = new Quantity(5m, UnitId);

        Assert.Equal(5m, qty.Amount);
        Assert.Equal(UnitId, qty.UnitId);
    }

    [Fact]
    public void Constructor_Rejects_Negative_Amount()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(-1m, UnitId));
        Assert.Contains("cannot be negative", ex.Message);
    }

    [Fact]
    public void Constructor_Allows_Zero_Amount()
    {
        var qty = new Quantity(0m, UnitId);

        Assert.Equal(0m, qty.Amount);
    }

    [Fact]
    public void WithAmount_Returns_New_Instance_With_Same_UnitId()
    {
        var original = new Quantity(3m, UnitId);

        var updated = original.WithAmount(7m);

        Assert.Equal(7m, updated.Amount);
        Assert.Equal(UnitId, updated.UnitId);
        Assert.NotSame(original, updated);
    }

    [Fact]
    public void Equality_Same_Values_Returns_True()
    {
        var a = new Quantity(5m, UnitId);
        var b = new Quantity(5m, UnitId);

        Assert.True(a == b);
    }

    [Fact]
    public void Equality_Different_Amount_Returns_False()
    {
        var a = new Quantity(5m, UnitId);
        var b = new Quantity(6m, UnitId);

        Assert.True(a != b);
    }

    [Fact]
    public void Equality_Different_Unit_Returns_False()
    {
        var a = new Quantity(5m, UnitId);
        var b = new Quantity(5m, Guid.NewGuid());

        Assert.True(a != b);
    }
}
