using Plantry.Web.Pages.Shared;

namespace Plantry.Tests.Web.Formatting;

/// <summary>
/// Unit tests for <see cref="RelativeTimeDisplay.Ago"/> (plantry-hp67) — the single source of
/// truth for the "today" / "N days ago" / "N weeks ago" bucketing shared by Tidy Up's dismissal
/// wording (<c>TidyUpDisplay.DismissedAgo</c>) and Take Stock's location freshness line
/// (<c>TakeStockDisplay.CountedAgo</c>). Both callers previously carried an independent copy of
/// this exact bucketing; extracted here so it cannot drift between them.
/// </summary>
public sealed class RelativeTimeDisplayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

    [Fact]
    public void Same_Day_Reads_Today()
    {
        var instant = Now.AddHours(-3);

        Assert.Equal("today", RelativeTimeDisplay.Ago(instant, Now));
    }

    [Theory]
    [InlineData(1, "1 day ago")]
    [InlineData(3, "3 days ago")]
    [InlineData(13, "13 days ago")]
    public void Under_Two_Weeks_Reads_In_Days(int daysAgo, string expected)
    {
        var instant = Now.AddDays(-daysAgo);

        Assert.Equal(expected, RelativeTimeDisplay.Ago(instant, Now));
    }

    [Theory]
    [InlineData(14, "2 weeks ago")]
    [InlineData(21, "3 weeks ago")]
    [InlineData(30, "4 weeks ago")]
    public void Two_Weeks_Or_More_Reads_In_Weeks(int daysAgo, string expected)
    {
        var instant = Now.AddDays(-daysAgo);

        Assert.Equal(expected, RelativeTimeDisplay.Ago(instant, Now));
    }
}
