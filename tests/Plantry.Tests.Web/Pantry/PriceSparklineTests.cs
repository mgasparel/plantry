using Plantry.Market.Application;
using Plantry.Web.Pages.Pantry.Products;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// Pure geometry tests for <see cref="PriceSparkline.BuildPoints"/> (plantry-fuej) — the product-detail
/// price-history sparkline's SVG <c>&lt;polyline&gt;</c> points string, independent of any page render.
/// </summary>
public sealed class PriceSparklineTests
{
    private static PriceHistoryPoint Point(int day, decimal unitPrice) =>
        new(new DateOnly(2026, 1, 1).AddDays(day), unitPrice);

    [Fact(DisplayName = "Fewer than two points yields no polyline (nothing to draw a trend through)")]
    public void BuildPoints_ReturnsEmpty_ForFewerThanTwoPoints()
    {
        Assert.Equal("", PriceSparkline.BuildPoints([]));
        Assert.Equal("", PriceSparkline.BuildPoints([Point(0, 3.00m)]));
    }

    [Fact(DisplayName = "Two points produce exactly two coordinate pairs spanning the full width")]
    public void BuildPoints_TwoPoints_SpansFullWidth()
    {
        var points = PriceSparkline.BuildPoints([Point(0, 1.00m), Point(1, 2.00m)]);

        var pairs = points.Split(' ');
        Assert.Equal(2, pairs.Length);
        Assert.StartsWith("0,", pairs[0]);
        Assert.StartsWith($"{PriceSparkline.Width},", pairs[1]);
    }

    [Fact(DisplayName = "A flat series (identical prices) plots a straight line through the vertical centre")]
    public void BuildPoints_FlatSeries_PlotsCentreLine()
    {
        var points = PriceSparkline.BuildPoints([Point(0, 5.00m), Point(1, 5.00m), Point(2, 5.00m)]);

        var ys = points.Split(' ').Select(p => decimal.Parse(p.Split(',')[1])).ToList();
        Assert.All(ys, y => Assert.Equal(PriceSparkline.Height / 2m, y));
    }

    [Fact(DisplayName = "A higher price plots at a smaller y (higher on screen — SVG y grows downward)")]
    public void BuildPoints_HigherPrice_PlotsAtSmallerY()
    {
        var points = PriceSparkline.BuildPoints([Point(0, 1.00m), Point(1, 10.00m)]);

        var ys = points.Split(' ').Select(p => decimal.Parse(p.Split(',')[1])).ToList();
        Assert.True(ys[1] < ys[0], "the later, higher-priced point must plot higher on screen (smaller y)");
    }

    [Fact(DisplayName = "Every plotted point stays within the viewBox bounds")]
    public void BuildPoints_StaysWithinViewBox()
    {
        var points = PriceSparkline.BuildPoints([Point(0, 1.00m), Point(1, 50.00m), Point(2, 0.50m), Point(3, 25.00m)]);

        foreach (var pair in points.Split(' '))
        {
            var parts = pair.Split(',');
            var x = decimal.Parse(parts[0]);
            var y = decimal.Parse(parts[1]);
            Assert.InRange(x, 0m, PriceSparkline.Width);
            Assert.InRange(y, 0m, PriceSparkline.Height);
        }
    }
}
