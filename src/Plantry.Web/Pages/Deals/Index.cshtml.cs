using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Deals.Application;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// The Deals page (DJ3 / §6a). Renders the household's <b>active</b> deals — grouped by store or category
/// via a client-side segmented toggle over the one <see cref="BrowseDeals"/> read result — with the
/// auto-matched marker (DL-O3), and a visually distinct <b>pending</b> section carrying the review count +
/// a Review entry that deep-links into the P5-8 queue. An empty household gets the subscribe-inviting
/// empty state (DJ1). The read model is clock-driven and nothing is stored.
/// </summary>
[Authorize]
public sealed class IndexModel(BrowseDeals browseDeals) : PageModel
{
    /// <summary>The route the pending-review "Review" entry deep-links into (the P5-8 queue).</summary>
    public const string ReviewQueueUrl = "/Deals/Review";

    public DealsBoard Board { get; private set; } = new([], []);

    /// <summary>Active deals grouped by store name (A→Z) for the "By store" segment.</summary>
    public IReadOnlyList<DealGroup> GroupedByStore { get; private set; } = [];

    /// <summary>Active deals grouped by product category ("Uncategorized" last) for the "By category" segment.</summary>
    public IReadOnlyList<DealGroup> GroupedByCategory { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        Board = await browseDeals.BrowseAsync(ct);

        GroupedByStore = Board.Active
            .GroupBy(d => d.StoreName)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DealGroup(g.Key, g.ToList()))
            .ToList();

        GroupedByCategory = Board.Active
            .GroupBy(d => d.CategoryLabel)
            // "Uncategorized" sorts last; every named category A→Z above it.
            .OrderBy(g => g.Key == "Uncategorized" ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DealGroup(g.Key, g.ToList()))
            .ToList();
    }
}

/// <summary>A labelled group of active deals — one store or one category — for the grouping toggle.</summary>
public sealed record DealGroup(string Label, IReadOnlyList<DealView> Deals);
