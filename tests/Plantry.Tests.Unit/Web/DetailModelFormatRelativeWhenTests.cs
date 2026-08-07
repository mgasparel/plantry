using PantryProductPage = Plantry.Web.Pages.Pantry.Products.DetailModel;

namespace Plantry.Tests.Unit.Web;

/// <summary>
/// Unit tests for the History table's relative-day wording (plantry-sbpk, rev 2 decision c — the
/// timeline prototype's one advantage kept in the When column). Pins the pure
/// <see cref="PantryProductPage.FormatRelativeWhen"/> helper directly with both a fixed "now" and a
/// fixed "then", mirroring <c>MarkOpenedToastTests</c>'s pattern of testing a page-model static
/// formatter without a full page render.
/// </summary>
public sealed class DetailModelFormatRelativeWhenTests
{
    private static readonly DateTimeOffset NowLocal = new(2026, 8, 6, 18, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "FormatRelativeWhen — same local date as now renders 'Today HH:mm'")]
    public void FormatRelativeWhen_SameDate_RendersToday()
    {
        var occurred = new DateTimeOffset(2026, 8, 6, 12, 40, 0, TimeSpan.Zero);

        var result = PantryProductPage.FormatRelativeWhen(occurred, NowLocal);

        Assert.Equal("Today 12:40", result);
    }

    [Fact(DisplayName = "FormatRelativeWhen — the day before now renders 'Yesterday HH:mm'")]
    public void FormatRelativeWhen_PreviousDate_RendersYesterday()
    {
        var occurred = new DateTimeOffset(2026, 8, 5, 19, 5, 0, TimeSpan.Zero);

        var result = PantryProductPage.FormatRelativeWhen(occurred, NowLocal);

        Assert.Equal("Yesterday 19:05", result);
    }

    [Fact(DisplayName = "FormatRelativeWhen — an older date renders the absolute 'd MMM HH:mm'")]
    public void FormatRelativeWhen_OlderDate_RendersAbsoluteDayMonth()
    {
        var occurred = new DateTimeOffset(2026, 8, 4, 18, 31, 0, TimeSpan.Zero);

        var result = PantryProductPage.FormatRelativeWhen(occurred, NowLocal);

        Assert.Equal("4 Aug 18:31", result);
    }
}
