namespace Plantry.Web.Pages.TidyUp;

/// <summary>Small display helpers for the Tidy Up page — kept out of the Housekeeping application layer,
/// which is a pure read model with no presentation concerns.</summary>
public static class TidyUpDisplay
{
    /// <summary>Renders a dismissal timestamp as "today" / "N days ago" / "N weeks ago" for the
    /// dismissed-disclosure row (prototype: "Dismissed 2 weeks ago").</summary>
    public static string DismissedAgo(DateTimeOffset dismissedAtUtc, DateTimeOffset nowUtc)
    {
        var span = nowUtc - dismissedAtUtc;
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
