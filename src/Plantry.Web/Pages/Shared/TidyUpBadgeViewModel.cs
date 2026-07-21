namespace Plantry.Web.Pages.Shared;

/// <summary>View model for <c>_TidyUpBadge.cshtml</c> — the shared Tidy Up nav count badge (T1/T6/T10).</summary>
/// <param name="Count">Open finding count; the badge renders nothing when this is 0 (T1).</param>
/// <param name="TargetId">The element id — distinct per rendering location (desktop sidebar vs. More hub) so an OOB response can update both independently.</param>
/// <param name="Oob">True when this render is an htmx out-of-band swap (dismiss/restore responses).</param>
public sealed record TidyUpBadgeViewModel(int Count, string TargetId, bool Oob);
