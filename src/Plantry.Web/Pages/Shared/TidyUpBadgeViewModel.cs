namespace Plantry.Web.Pages.Shared;

/// <summary>
/// Which visual form <c>_TidyUpBadge.cshtml</c> renders when <see cref="TidyUpBadgeViewModel.Count"/>
/// is greater than zero (plantry-kdvi).
/// </summary>
public enum TidyUpBadgeVariant
{
    /// <summary>The numeric pill (<c>.sidebar__count</c>) — desktop sidebar and the More sheet's Tidy Up row.</summary>
    Count,

    /// <summary>A small unread dot (<c>.bottom-nav__dot</c>) — the mobile bottom-nav More item. A specific
    /// number would read as more urgency than Tidy Up findings actually carry.</summary>
    Dot,
}

/// <summary>View model for <c>_TidyUpBadge.cshtml</c> — the shared Tidy Up nav count badge (T1/T6/T10).</summary>
/// <param name="Count">Open finding count; the badge renders nothing when this is 0 (T1).</param>
/// <param name="TargetId">The element id — distinct per rendering location (desktop sidebar, More sheet, bottom-nav dot) so an OOB response can update all of them independently.</param>
/// <param name="Oob">True when this render is an htmx out-of-band swap (dismiss/restore responses).</param>
/// <param name="Variant">Which visual form to render when <paramref name="Count"/> is greater than zero. Defaults to <see cref="TidyUpBadgeVariant.Count"/> for the existing sidebar/sheet call sites.</param>
public sealed record TidyUpBadgeViewModel(int Count, string TargetId, bool Oob, TidyUpBadgeVariant Variant = TidyUpBadgeVariant.Count);
