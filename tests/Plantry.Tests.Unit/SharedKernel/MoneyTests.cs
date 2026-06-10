using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.SharedKernel;

public sealed class MoneyTests
{
    private static readonly Guid AnyUnit = Guid.NewGuid();

    [Fact]
    public void Constructor_Stores_MinorUnits_And_Uppercases_Currency()
    {
        var money = new Money(123, "gbp");

        Assert.Equal(123, money.MinorUnits);
        Assert.Equal("GBP", money.Currency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_Rejects_Null_Or_Whitespace_Currency(string? currency)
    {
        // null → ArgumentNullException (subtype); empty/whitespace → ArgumentException
        Assert.ThrowsAny<ArgumentException>(() => new Money(0, currency!));
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("GBPA")]
    public void Constructor_Rejects_Non_Three_Letter_Currency(string currency)
    {
        var ex = Assert.Throws<ArgumentException>(() => new Money(0, currency));
        Assert.Contains("3-letter ISO 4217", ex.Message);
    }

    [Fact]
    public void Zero_Returns_Zero_Minor_Units_With_Correct_Currency()
    {
        var money = Money.Zero("USD");

        Assert.Equal(0, money.MinorUnits);
        Assert.Equal("USD", money.Currency);
    }

    [Fact]
    public void FromDecimal_Rounds_AwayFromZero()
    {
        var money = Money.FromDecimal(1.005m, "GBP", decimalPlaces: 2);

        Assert.Equal(101, money.MinorUnits);
    }

    [Fact]
    public void ToDecimal_Round_Trips_From_FromDecimal()
    {
        var money = Money.FromDecimal(1.23m, "GBP");

        Assert.Equal(1.23m, money.ToDecimal());
    }

    [Fact]
    public void Add_Same_Currency_Sums_Minor_Units()
    {
        var a = new Money(100, "GBP");
        var b = new Money(50, "GBP");

        var result = a.Add(b);

        Assert.Equal(150, result.MinorUnits);
        Assert.Equal("GBP", result.Currency);
    }

    [Fact]
    public void Add_Different_Currencies_Throws()
    {
        var gbp = new Money(100, "GBP");
        var usd = new Money(100, "USD");

        var ex = Assert.Throws<InvalidOperationException>(() => gbp.Add(usd));
        Assert.Contains("Cannot mix currencies", ex.Message);
    }

    [Fact]
    public void Subtract_Same_Currency_Differences_Minor_Units()
    {
        var a = new Money(100, "GBP");
        var b = new Money(30, "GBP");

        var result = a.Subtract(b);

        Assert.Equal(70, result.MinorUnits);
        Assert.Equal("GBP", result.Currency);
    }

    [Fact]
    public void Subtract_Different_Currencies_Throws()
    {
        var gbp = new Money(100, "GBP");
        var usd = new Money(50, "USD");

        Assert.Throws<InvalidOperationException>(() => gbp.Subtract(usd));
    }

    [Fact]
    public void Equality_Same_Values_Returns_True()
    {
        var a = new Money(100, "GBP");
        var b = new Money(100, "GBP");

        Assert.True(a == b);
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Equality_Different_Values_Returns_False()
    {
        var a = new Money(100, "GBP");
        var b = new Money(200, "GBP");

        Assert.True(a != b);
    }

    [Fact]
    public void Equality_Different_Currency_Returns_False()
    {
        var a = new Money(100, "GBP");
        var b = new Money(100, "USD");

        Assert.True(a != b);
    }

    [Fact]
    public void Equals_Null_Returns_False()
    {
        var money = new Money(100, "GBP");

        Assert.False(money.Equals(null));
    }

    [Fact]
    public void Equals_Different_ValueObject_Type_Returns_False()
    {
        var money = new Money(100, "GBP");
        var qty = new Quantity(1m, Guid.NewGuid());

        Assert.False(money.Equals(qty));
    }
}
