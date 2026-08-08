using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.TidyUp;

/// <summary>Small display helpers for the Tidy Up page — kept out of the Housekeeping application layer,
/// which is a pure read model with no presentation concerns.</summary>
public static class TidyUpDisplay
{
    /// <summary>Renders a dismissal timestamp as "today" / "N days ago" / "N weeks ago" for the
    /// dismissed-disclosure row (prototype: "Dismissed 2 weeks ago"). Delegates the day/week
    /// bucketing to the shared house rule in <see cref="RelativeTimeDisplay.Ago"/> (plantry-hp67
    /// — Take Stock's freshness line reuses the identical rule).</summary>
    public static string DismissedAgo(DateTimeOffset dismissedAtUtc, DateTimeOffset nowUtc) =>
        RelativeTimeDisplay.Ago(dismissedAtUtc, nowUtc);
}
