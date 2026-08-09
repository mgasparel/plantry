using Plantry.Market.Application;
using Xunit;

namespace Plantry.Tests.Unit.Market.Deals.Application;

/// <summary>
/// L1 tests for <see cref="PurchaseCadence.AverageInterval"/> (plantry-gtgl, Deals-review purchase
/// context) — the "you buy this every ~3 weeks" cadence estimate over raw purchase-journal timestamps.
/// </summary>
public sealed class PurchaseCadenceTests
{
    [Fact(DisplayName = "Two purchases three weeks apart average to a 3-week interval")]
    public void Two_Purchases_Three_Weeks_Apart()
    {
        var dates = new List<DateTimeOffset>
        {
            new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
        };

        var interval = PurchaseCadence.AverageInterval(dates);

        Assert.Equal(TimeSpan.FromDays(21), interval);
    }

    [Fact(DisplayName = "Three evenly-spaced purchases average the gaps, not just the last one")]
    public void Three_Purchases_Averages_The_Gaps()
    {
        var dates = new List<DateTimeOffset>
        {
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 1, 11, 0, 0, 0, TimeSpan.Zero), // +10 days
            new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero), // +20 days
        };

        var interval = PurchaseCadence.AverageInterval(dates);

        Assert.Equal(TimeSpan.FromDays(15), interval); // (10+20)/2 gaps == 30/2 total span/gaps
    }

    [Fact(DisplayName = "Unordered input is sorted internally — order never affects the result")]
    public void Order_Independent()
    {
        var ordered = new List<DateTimeOffset>
        {
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 1, 22, 0, 0, 0, TimeSpan.Zero),
        };
        var reversed = new List<DateTimeOffset>(ordered);
        reversed.Reverse();

        Assert.Equal(PurchaseCadence.AverageInterval(ordered), PurchaseCadence.AverageInterval(reversed));
    }

    [Fact(DisplayName = "A single purchase has no interval to measure — null")]
    public void Single_Purchase_Returns_Null()
    {
        Assert.Null(PurchaseCadence.AverageInterval([new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)]));
    }

    [Fact(DisplayName = "No purchases at all — null")]
    public void No_Purchases_Returns_Null()
    {
        Assert.Null(PurchaseCadence.AverageInterval([]));
    }
}
