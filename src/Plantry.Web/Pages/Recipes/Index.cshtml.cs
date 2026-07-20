using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;

namespace Plantry.Web.Pages.Recipes;

[Authorize]
public sealed class IndexModel(BrowseRecipesQuery query, DisplayCurrencyAccessor displayCurrency) : PageModel
{
    /// <summary>Household display currency (plantry-2x6e.2) — per-recipe cost-per-serving renders through MoneyDisplay with it.</summary>
    public string DisplayCurrency { get; private set; } = "USD";

    // ── Bind parameters ──────────────────────────────────────────────────────

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? TagId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Soon { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Desc { get; set; } = true;

    // ── View model ───────────────────────────────────────────────────────────

    public BrowseRecipesResult Result { get; private set; } = null!;

    /// <summary>
    /// Tag color palette — deterministic per tag name (first match in normalized name).
    /// Matches the canonical Browse prototype palette. Used by the tag filter chips and
    /// the per-recipe mini-pills / grid tag text.
    /// </summary>
    public static string TagColor(string tagName) => tagName.ToLowerInvariant() switch
    {
        var n when n.Contains("fish")       => "#4b9cd3",
        var n when n.Contains("poultry")    => "#e07b39",
        var n when n.Contains("meat")       => "#c0392b",
        var n when n.Contains("vegetarian") => "#27ae60",
        var n when n.Contains("vegan")      => "#1abc9c",
        var n when n.Contains("dairy")      => "#8e44ad",
        var n when n.Contains("spicy")      => "#e74c3c",
        _                                   => "#9c9587",
    };

    /// <summary>Fulfillment level class suffix based on percentage (hi/mid/lo).</summary>
    public static string FulfLevel(int pct) => pct >= 80 ? "hi" : pct >= 50 ? "mid" : "lo";

    /// <summary>Formats a cook time in minutes to a human-readable string.</summary>
    public static string FmtTime(int? minutes) => minutes switch
    {
        null => "—",
        < 60 => $"{minutes} min",
        _ => $"{minutes / 60}h {minutes % 60}m",
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var sort = Sort?.ToLowerInvariant() switch
        {
            "cost"    => BrowseSort.Cost,
            "cooktime"  => BrowseSort.CookTime,
            "recent"  => BrowseSort.RecentlyAdded,
            "name"    => BrowseSort.Name,
            _         => BrowseSort.Fulfillment,
        };

        // Default: Fulfillment descending (issue spec).
        var descending = Sort is null ? true : Desc;

        var filter = new BrowseRecipesFilter(
            NameQuery: Q,
            TagId: TagId,
            UseSoon: Soon,
            Sort: sort,
            SortDescending: descending);

        Result = await query.ExecuteAsync(filter, ct);

        // Household display currency for the per-recipe cost cells (plantry-2x6e.2); one resolve for the
        // full page and the htmx results-fragment swap below.
        DisplayCurrency = await displayCurrency.GetAsync(ct);

        // htmx partial swap: filter/search/sort requests target #recipes-results and expect
        // only the results fragment — not the full page with layout chrome. Returning the full
        // Page() would inject the entire document tree inside #recipes-results. The established
        // project pattern (see Intake/Review.cshtml.cs) is to detect HX-Request and return a partial.
        if (Request.Headers.ContainsKey("HX-Request"))
            return Partial("_RecipesBrowseResults", this);

        return Page();
    }

    // ── Tag helpers for the view ─────────────────────────────────────────────

    /// <summary>
    /// Returns the display name for a tag id from the result's AllTags list.
    /// Used by recipe cards to render tag mini-pills / grid tag text.
    /// </summary>
    public string TagName(Guid tagId) =>
        Result.AllTags.FirstOrDefault(t => t.Id.Value == tagId)?.Name ?? string.Empty;
}
