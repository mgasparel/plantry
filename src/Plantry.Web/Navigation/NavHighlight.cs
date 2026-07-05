namespace Plantry.Web.Navigation;

/// <summary>
/// Pure nav-highlight predicate for the main sidebar links in <c>_Layout.cshtml</c>.
///
/// <para>
/// A link is "active" when the current Razor page path is under its <paramref name="prefix"/>,
/// unless it also falls under an <paramref name="except"/> sub-tree that a sibling link owns.
/// The <c>except</c> carve-out exists because Take&#160;Stock lives under the Pantry route tree
/// (<c>/Pantry/TakeStock/*</c>): without it, visiting Take&#160;Stock would light up both the
/// Pantry and the Take&#160;Stock links. This regressed once, which is why the logic is extracted
/// here as a unit-testable static method rather than living inside a <c>.cshtml</c> local function.
/// </para>
///
/// <para>
/// Matching is prefix-based and case-insensitive, mirroring how ASP.NET Core surfaces the
/// <c>page</c> route value (e.g. <c>/Pantry/Index</c>) independent of the request URL's casing.
/// </para>
/// </summary>
public static class NavHighlight
{
    /// <summary>
    /// Returns <see langword="true"/> when the nav link for <paramref name="prefix"/> should be
    /// rendered active for the given current <paramref name="page"/>.
    /// </summary>
    /// <param name="page">
    /// The current Razor page path from <c>RouteData.Values["page"]</c> (e.g. <c>/Pantry/Index</c>),
    /// or <see langword="null"/> when unavailable — a null page is never active.
    /// </param>
    /// <param name="prefix">The route prefix the link represents (e.g. <c>/Pantry</c>).</param>
    /// <param name="except">
    /// An optional nested prefix owned by a sibling link (e.g. <c>/Pantry/TakeStock</c>). When the
    /// current page falls under it, this link yields to the sibling and is not active.
    /// </param>
    public static bool IsActive(string? page, string prefix, string? except = null)
    {
        if (page is null || !page.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (except is not null && page.StartsWith(except, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
