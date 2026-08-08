using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.Pantry.TakeStock;

/// <summary>Small display helper for Take Stock freshness — kept out of the Pantry application
/// layer, which is a pure read model with no presentation concerns (mirrors
/// <see cref="Plantry.Web.Pages.TidyUp.TidyUpDisplay"/>).</summary>
public static class TakeStockDisplay
{
    /// <summary>Renders a location's last-counted timestamp as "Never counted" / "Counted today" /
    /// "Counted N days ago" / "Counted N weeks ago" for the location picker cards (plantry-hp67)
    /// and the walk header sub-line. The relative-time bucketing itself is the shared house rule
    /// in <see cref="RelativeTimeDisplay.Ago"/> — only the "Never counted" null-case and the
    /// "Counted " prefix are specific to this surface.</summary>
    public static string CountedAgo(DateTimeOffset? lastCountedAtUtc, DateTimeOffset nowUtc)
    {
        if (lastCountedAtUtc is not { } counted)
            return "Never counted";

        return $"Counted {RelativeTimeDisplay.Ago(counted, nowUtc)}";
    }
}
