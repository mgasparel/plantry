using Plantry.Web.Pages.Pantry.TakeStock;

namespace Plantry.Tests.Web.TakeStock;

/// <summary>Unit coverage for <see cref="TakeStockDisplay.CountedAgo"/> (plantry-hp67) — the
/// freshness text shown on the location picker cards and the walk header sub-line. The day/week
/// relative-time bucketing itself is the shared house rule covered by
/// <c>RelativeTimeDisplayTests</c> (tests/Plantry.Tests.Web/Formatting) — this suite covers only
/// what is specific to this surface: the null "Never counted" case and the "Counted " prefix.</summary>
public sealed class TakeStockDisplayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

    [Fact]
    public void Null_LastCountedAt_Reads_Never_Counted()
    {
        Assert.Equal("Never counted", TakeStockDisplay.CountedAgo(null, Now));
    }

    [Fact]
    public void NonNull_LastCountedAt_Is_Prefixed_With_Counted()
    {
        var lastCounted = Now.AddDays(-3);

        Assert.Equal("Counted 3 days ago", TakeStockDisplay.CountedAgo(lastCounted, Now));
    }
}
