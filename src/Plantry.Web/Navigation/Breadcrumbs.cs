using System.Text;

namespace Plantry.Web.Navigation;

/// <summary>
/// Pure breadcrumb-label, parent-chain, and icon logic for the topbar crumb in
/// <c>_Layout.cshtml</c>.
///
/// <para>
/// This mirrors the <see cref="NavHighlight"/> precedent: edge-case-laden string logic
/// (PascalCase humanisation, uniform leaf-drop, action-page folder dedup, and the
/// parents-first-then-segments icon fallback) is extracted out of the <c>.cshtml</c>
/// inline locals into a pure, unit-testable static class that takes plain strings and
/// carries no <c>ViewContext</c> / DI dependency.
/// </para>
/// </summary>
public static class Breadcrumbs
{
    // Explicit display labels for route segments whose PascalCase-split humanisation would
    // be wrong or awkward (e.g. "MealPlan" -> "Meal Plan", "Dev" -> "Components").
    private static readonly Dictionary<string, string> CrumbLabels = new()
    {
        ["Today"] = "Today",
        ["Pantry"] = "Pantry",
        ["Intake"] = "Intake",
        ["Shopping"] = "Shopping",
        ["Catalog"] = "Catalog",
        ["Recipes"] = "Recipes",
        ["Products"] = "Products",
        ["Categories"] = "Categories",
        ["Units"] = "Units",
        ["Locations"] = "Locations",
        ["Settings"] = "Settings",
        ["MealPlan"] = "Meal Plan",
        ["More"] = "More",
        ["Dev"] = "Components",
        ["Import"] = "Grocy Import",
    };

    // Icon sprite name (referenced as #i-NAME) keyed by the top-level route segment.
    private static readonly Dictionary<string, string> CrumbIcons = new()
    {
        ["Today"] = "sun",
        ["Pantry"] = "pantry",
        ["Intake"] = "receipt",
        ["Shopping"] = "cart",
        ["Catalog"] = "tag",
        ["Recipes"] = "recipe",
        ["MealPlan"] = "calendar",
        ["Settings"] = "settings",
        ["More"] = "grid",
        ["Dev"] = "components",
        ["Import"] = "import",
    };

    // Trailing route segments that denote an action rather than a named leaf page. These
    // drive the folder-dedup rule in <see cref="BuildParents"/>.
    private static readonly HashSet<string> ActionNames = new() { "Index", "Create", "Edit", "Detail" };

    /// <summary>
    /// Maps a route segment to its display label via the explicit dictionary, falling back
    /// to splitting PascalCase into words so raw route names (e.g. <c>TakeStock</c>) never
    /// leak. A null or empty segment is returned unchanged.
    /// </summary>
    public static string Label(string segment)
    {
        if (CrumbLabels.TryGetValue(segment, out var mapped))
        {
            return mapped;
        }
        if (string.IsNullOrEmpty(segment))
        {
            return segment;
        }
        var sb = new StringBuilder(segment.Length + 4);
        for (var i = 0; i < segment.Length; i++)
        {
            if (i > 0 && char.IsUpper(segment[i]) && !char.IsUpper(segment[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(segment[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the parent-crumb chain for a Razor page path.
    ///
    /// <para>
    /// The trailing segment always denotes the current page — rendered as the bold title —
    /// so it is never a parent crumb. For action pages (Index/Detail/Edit/Create) the action
    /// word is the trailing segment; for a named leaf page (e.g. <c>Settings/Tags</c>) the
    /// page name is. Dropping it uniformly removes the duplicated leaf crumb in both cases.
    /// </para>
    ///
    /// <para>
    /// For folder index pages (e.g. <c>…/Products/Index</c>) the remaining leaf is the
    /// containing folder, whose label usually equals the title — drop it too so the crumb
    /// isn't repeated.
    /// </para>
    /// </summary>
    /// <param name="pagePath">The current Razor page path (e.g. <c>/Catalog/Products/Index</c>).</param>
    /// <param name="title">The page title (<c>ViewData["Title"]</c>), used for the folder-dedup check.</param>
    public static IReadOnlyList<string> BuildParents(string pagePath, string title)
    {
        var segments = pagePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parents = segments.Length > 0 ? segments[..^1] : segments;
        if (segments.Length > 0 && ActionNames.Contains(segments[^1])
            && parents.Length > 0
            && string.Equals(Label(parents[^1]), title, StringComparison.OrdinalIgnoreCase))
        {
            parents = parents[..^1];
        }
        return parents;
    }

    /// <summary>
    /// Selects the crumb icon sprite name. Prefers the first parent crumb's icon; when there
    /// is no parent (a single-segment page), falls back to the first raw route segment. This
    /// parents-first-then-segments fallback is preserved exactly from the original inline logic.
    /// </summary>
    /// <param name="parents">The parent-crumb chain from <see cref="BuildParents"/>.</param>
    /// <param name="segments">The raw route segments of the page path.</param>
    /// <returns>The icon sprite name (without the <c>i-</c> prefix), or <see langword="null"/> when none maps.</returns>
    public static string? Icon(IReadOnlyList<string> parents, IReadOnlyList<string> segments)
    {
        return parents.Count > 0
            ? CrumbIcons.GetValueOrDefault(parents[0])
            : CrumbIcons.GetValueOrDefault(segments.Count > 0 ? segments[0] : "");
    }
}
