namespace Plantry.Web.Pages.Shared;

/// <summary>
/// The single source of truth for the house "N days/weeks ago" relative-time wording used across
/// every surface that shows a past-timestamp freshness signal — Tidy Up's dismissal disclosure
/// (<see cref="Plantry.Web.Pages.TidyUp.TidyUpDisplay.DismissedAgo"/>) and the Take Stock
/// location freshness line (<see cref="Plantry.Web.Pages.Pantry.TakeStock.TakeStockDisplay.CountedAgo"/>,
/// plantry-hp67). Each surface previously rolled its own copy of the identical day/week bucketing
/// and pluralisation; both now delegate here so the rule can't drift between them (code review,
/// ".claude/CLAUDE.md" — "extract before you repeat").
/// </summary>
public static class RelativeTimeDisplay
{
    /// <summary>
    /// Renders a past instant as "today" / "N day(s) ago" / "N week(s) ago" relative to
    /// <paramref name="nowUtc"/>. Both callers wrap this with their own surface-specific prefix
    /// ("Dismissed …", "Counted …") and null-handling (Take Stock's "Never counted").
    /// </summary>
    public static string Ago(DateTimeOffset instantUtc, DateTimeOffset nowUtc)
    {
        var span = nowUtc - instantUtc;
        if (span.TotalDays < 1)
            return "today";
        if (span.TotalDays < 14)
        {
            var days = (int)span.TotalDays;
            return $"{days} day{(days == 1 ? "" : "s")} ago";
        }

        var weeks = (int)(span.TotalDays / 7);
        return $"{weeks} week{(weeks == 1 ? "" : "s")} ago";
    }
}
