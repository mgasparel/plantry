using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L1 tests for <see cref="FlyerPullResult.DedupContent"/> — the canonical DD5 dedup hash input
/// (plantry-04ji.4). The projection must be immune to what does NOT change a deal (the verbatim raw Flipp
/// payload's volatile chrome, item ordering) yet sensitive to what does (price, brand, size, sale story,
/// added/removed items, the flyer window, the external id). It is what makes an unchanged flyer hash
/// identically across daily pulls so the worker skips re-staging + a wasted AI-matcher pass.
/// </summary>
public sealed class FlyerPullResultDedupTests
{
    private static ValidityWindow Window(int fromDay = 1, int toDay = 7) =>
        ValidityWindow.Create(new DateOnly(2026, 7, fromDay), new DateOnly(2026, 7, toDay)).Value;

    private static RawDeal Deal(
        string name, decimal price = 4.99m, string? brand = null, string? size = null,
        decimal? qty = null, string? saleStory = null) =>
        new(name, brand, size, price, qty, UnitId: null, saleStory, Window());

    private static FlyerPullResult Pull(string rawContent, params RawDeal[] deals) =>
        new("flyer-1", Window(), rawContent, deals);

    [Fact]
    public void DedupContent_Is_Immune_To_Volatile_RawContent()
    {
        // Same advertised deals; the raw items payload differs (impression counters, generated timestamps).
        var a = Pull("[{\"name\":\"Bread\",\"impressions\":11}]", Deal("Bread", 2.49m));
        var b = Pull("[{\"name\":\"Bread\",\"impressions\":9999,\"ts\":\"2026-07-06T02:00:00Z\"}]", Deal("Bread", 2.49m));

        Assert.Equal(a.DedupContent, b.DedupContent);
    }

    [Fact]
    public void DedupContent_Is_Order_Normalized()
    {
        var a = Pull("x", Deal("Apples"), Deal("Bread"), Deal("Cheese"));
        var b = Pull("y", Deal("Cheese"), Deal("Apples"), Deal("Bread"));

        Assert.Equal(a.DedupContent, b.DedupContent);
    }

    [Fact]
    public void DedupContent_Changes_When_A_Price_Changes()
    {
        var a = Pull("x", Deal("Milk", price: 3.00m));
        var b = Pull("x", Deal("Milk", price: 2.50m));

        Assert.NotEqual(a.DedupContent, b.DedupContent);
    }

    [Fact]
    public void DedupContent_Changes_When_A_Deal_Is_Added()
    {
        var a = Pull("x", Deal("Milk"));
        var b = Pull("x", Deal("Milk"), Deal("Eggs"));

        Assert.NotEqual(a.DedupContent, b.DedupContent);
    }

    [Fact]
    public void DedupContent_Distinguishes_Deals_Differing_Only_In_Brand_Size_Or_SaleStory()
    {
        var baseline = Pull("x", Deal("Milk"));
        var brand = Pull("x", Deal("Milk", brand: "Beatrice"));
        var size = Pull("x", Deal("Milk", size: "4 L"));
        var story = Pull("x", Deal("Milk", saleStory: "2 for $5"));

        Assert.NotEqual(baseline.DedupContent, brand.DedupContent);
        Assert.NotEqual(baseline.DedupContent, size.DedupContent);
        Assert.NotEqual(baseline.DedupContent, story.DedupContent);
    }

    [Fact]
    public void DedupContent_Changes_When_The_Window_Changes()
    {
        var a = new FlyerPullResult("flyer-1", Window(1, 7), "x", [Deal("Milk")]);
        var b = new FlyerPullResult("flyer-1", Window(8, 14), "x", [Deal("Milk")]);

        Assert.NotEqual(a.DedupContent, b.DedupContent);
    }

    [Fact]
    public void DedupContent_Changes_When_The_FlyerExternalId_Changes()
    {
        var a = new FlyerPullResult("flyer-1", Window(), "x", [Deal("Milk")]);
        var b = new FlyerPullResult("flyer-2", Window(), "x", [Deal("Milk")]);

        Assert.NotEqual(a.DedupContent, b.DedupContent);
    }
}
