namespace Plantry.Web.Pages.Shared;

/// <summary>
/// The single source of truth for expiry <b>wording</b> and <b>colour tier</b> across every surface that
/// shows a soonest-expiry signal — the Today expiring-soon rail, Recipe ingredient rows, and the Pantry grid
/// (plantry-fdoq). Before this, each surface rolled its own copy of the label + tier logic and they drifted
/// ("Expired" vs "Expired 25d ago" vs a bare "25 Jun"; 3-tier vs binary colour). Every surface now renders the
/// canonical <c>.badge-expiry</c> pill driven by this helper, so the relative wording and the existing 3-tier
/// colour scale are identical everywhere.
/// </summary>
/// <remarks>
/// This is a Web <i>presentation</i> concern (wording + CSS tier modifier), deliberately separate from
/// <c>Plantry.Inventory.Application.ExpiryTone</c> which is the domain classification (None/Ok/Soon/Expired,
/// baked against the per-household "expiring soon" horizon). A surface first decides <i>whether</i> to show a
/// pill using <c>ExpiryTone</c> (Pantry does this), then asks this helper <i>what</i> the pill says and which
/// colour tier it wears.
/// </remarks>
public static class ExpiryDisplay
{
    /// <summary>
    /// The wording + colour tier for a soonest-expiry date relative to today. Everything is a pure function of
    /// the signed day delta (<c>soonestExpiry - today</c>), so the two overloads share one implementation.
    /// </summary>
    /// <param name="soonestExpiry">The soonest active-lot expiry date.</param>
    /// <param name="today">Today's date, in the same calendar the caller uses to compute expiry.</param>
    /// <returns>
    /// <c>Label</c> — the relative wording ("Expired 25d ago" / "Today" / "Tomorrow" / "in 6d"); and
    /// <c>TierModifier</c> — one of <c>"urgent"</c>, <c>"soon"</c>, <c>"ok"</c>, matching the
    /// <c>.badge-expiry--{tier}</c> CSS modifiers.
    /// </returns>
    public static (string Label, string TierModifier) Format(DateOnly soonestExpiry, DateOnly today) =>
        FromDaysUntilExpiry(soonestExpiry.DayNumber - today.DayNumber);

    /// <summary>
    /// The wording + colour tier from the <b>signed days until expiry</b> directly — negative = already past
    /// (expired), 0 = expires today, positive = days remaining. This is the entry the Recipe ingredient rows
    /// use: <c>IngredientFulfillment.ExpiresWithinDays</c> already carries exactly this signed, horizon-clamped
    /// delta, so the view need not re-derive (or re-plumb) the expiry date and "today" just to reuse the wording.
    /// </summary>
    /// <remarks>
    /// Tier reuses the Today rail's existing thresholds verbatim (plantry-fdoq, no new numbers): urgent when the
    /// item is expired or ≤1 day out; soon when ≤3 days out; ok at ≥4 days. Because a same-day expiry is treated
    /// as not-yet-past everywhere in Plantry (the Today read model's <c>IsExpired = date &lt; today</c> is
    /// strict), <paramref name="daysUntilExpiry"/> of 0 renders "Today"; the expired branch (negative) therefore
    /// always has a day count of at least 1.
    /// </remarks>
    public static (string Label, string TierModifier) FromDaysUntilExpiry(int daysUntilExpiry)
    {
        var tier = daysUntilExpiry <= 1 ? "urgent"
                 : daysUntilExpiry <= 3 ? "soon"
                 : "ok";

        var label = daysUntilExpiry < 0 ? $"Expired {-daysUntilExpiry}d ago"
                  : daysUntilExpiry == 0 ? "Today"
                  : daysUntilExpiry == 1 ? "Tomorrow"
                  : $"in {daysUntilExpiry}d";

        return (label, tier);
    }
}
