using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// The Deals page (DJ3 / §6a). Renders the household's <b>active</b> deals — grouped by store or category
/// via a client-side segmented toggle over the one <see cref="BrowseDeals"/> read result — with the
/// auto-matched marker (DL-O3), and a visually distinct <b>pending</b> section carrying the review count +
/// a Review entry that deep-links into the P5-8 queue. An empty household gets the subscribe-inviting
/// empty state (DJ1). The <b>stock-up alerts</b> surface (P5-10 / DJ5) sits above the active list: products
/// the household frequently buys that currently have an active deal, each with an "Add to list" action that
/// reuses the P2-4 Shopping seam. Every read model is clock-driven and nothing is stored.
/// </summary>
[Authorize]
public sealed class IndexModel(
    BrowseDeals browseDeals,
    StockUpAlerts stockUpAlerts,
    IDealShoppingListWriter shoppingWriter) : PageModel
{
    /// <summary>The route the pending-review "Review" entry deep-links into (the P5-8 queue).</summary>
    public const string ReviewQueueUrl = "/Deals/Review";

    public DealsBoard Board { get; private set; } = new([], []);

    /// <summary>Stock-up alerts (P5-10 / DJ5): frequently-bought products that currently have an active deal.</summary>
    public IReadOnlyList<StockUpAlert> Alerts { get; private set; } = [];

    /// <summary>Active deals grouped by store name (A→Z) for the "By store" segment.</summary>
    public IReadOnlyList<DealGroup> GroupedByStore { get; private set; } = [];

    /// <summary>Active deals grouped by product category ("Uncategorized" last) for the "By category" segment.</summary>
    public IReadOnlyList<DealGroup> GroupedByCategory { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        Board = await browseDeals.BrowseAsync(ct);
        // Reuse the single Board read above — StockUpAlerts computes over the already-materialised active
        // partition rather than re-running the whole BrowseDeals pipeline a second time (plantry-k7tc).
        Alerts = await stockUpAlerts.ComputeAsync(Board.Active, ct);

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

    /// <summary>
    /// htmx POST handler for a stock-up alert's "Add to list" action (DJ5). Places the alert's product on
    /// the shopping list via the reused P2-4 seam with <c>source="deal"</c> + <c>source_ref=dealId</c>;
    /// Shopping's merge rule keeps an already-listed item from duplicating. Returns 200 on success so the
    /// button flips to its "Added" confirm state client-side (no page reload).
    /// </summary>
    public async Task<IActionResult> OnPostAddToListAsync(Guid productId, Guid dealId, CancellationToken ct = default)
    {
        if (productId == Guid.Empty || dealId == Guid.Empty)
            return BadRequest();

        await shoppingWriter.AddItemAsync(productId, DealId.From(dealId), ct);
        return new OkResult();
    }
}

/// <summary>A labelled group of active deals — one store or one category — for the grouping toggle.</summary>
public sealed record DealGroup(string Label, IReadOnlyList<DealView> Deals);
