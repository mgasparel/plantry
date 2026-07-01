using Plantry.Deals.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Application;

/// <summary>
/// A single browsable deal, projected for the Deals page (DJ3 / §6a). <see cref="ProductName"/> and
/// <see cref="CategoryName"/> are resolved for a confirmed (active) deal; a pending deal has no committed
/// product yet, so both are null and <see cref="DisplayName"/> falls back to the raw flyer name. The raw
/// flyer fields stay verbatim (ACL quarantine, DD6) — nothing here is ever written back.
/// </summary>
public sealed record DealView(
    DealId DealId,
    Guid StoreId,
    string StoreName,
    Guid? ProductId,
    string? ProductName,
    string? CategoryName,
    string RawName,
    string? Brand,
    string? SaleStory,
    decimal Price,
    decimal? Quantity,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    bool AutoMatched)
{
    /// <summary>Resolved product name for a confirmed deal, else the raw flyer name (a pending, unresolved item).</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(ProductName) ? RawName : ProductName;

    /// <summary>The category-grouping bucket — the product's category, or "Uncategorized" when it has none.</summary>
    public string CategoryLabel => string.IsNullOrWhiteSpace(CategoryName) ? "Uncategorized" : CategoryName;
}

/// <summary>
/// The Deals page read model (DJ3): the household's <b>active</b> deals (browsable now) and the
/// <b>pending</b> review queue, both computed fresh against the clock — nothing about "is active" is
/// stored (DD7/DD14). Grouping (store vs category) is a presentation concern over <see cref="Active"/>,
/// so it lives in the page, not here.
/// </summary>
public sealed record DealsBoard(
    IReadOnlyList<DealView> Active,
    IReadOnlyList<DealView> Pending)
{
    /// <summary>The pending-review count — recomputed <c>Pending ∧ in-window</c>, never a stored/stamped count (DD14).</summary>
    public int PendingCount => Pending.Count;

    /// <summary>True when the household has no active or pending deals — drives the subscribe-inviting empty state (DJ1).</summary>
    public bool IsEmpty => Active.Count == 0 && Pending.Count == 0;
}

/// <summary>
/// <c>BrowseDeals</c> read service (§7 / DJ3). Read-only over the <see cref="Deal"/> aggregate and the
/// clock — <b>nothing is stored</b>. Partitions the household's deals into the <b>active</b> set
/// (<see cref="DealStatus.Confirmed"/> ∧ in-window, DD7) and the <b>pending</b> queue
/// (<see cref="DealStatus.Pending"/> ∧ <c>today ≤ valid_to</c>, DD14 — expired-unreviewed deals silently
/// drop off), both computed against <see cref="IClock"/> today so a deal activates/expires as the clock
/// moves with no write. Resolves product + store display names via batch reads (no N+1) and surfaces the
/// auto-matched marker (DL-O3). This is a normal RLS-scoped HTTP request, so the underlying context only
/// ever sees the signed-in household's rows.
/// </summary>
public sealed class BrowseDeals(
    IDealRepository deals,
    ICatalogProductReader products,
    ICatalogStoreReader stores,
    IClock clock)
{
    private static readonly IReadOnlyDictionary<Guid, DealProductInfo> NoProducts =
        new Dictionary<Guid, DealProductInfo>();

    public async Task<DealsBoard> BrowseAsync(CancellationToken ct = default)
    {
        var all = await deals.ListBrowsableAsync(ct);
        if (all.Count == 0)
            return new DealsBoard([], []);

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // Clock-driven partition (never stored). DD7: active = Confirmed ∧ within window.
        var active = all
            .Where(d => d.Status == DealStatus.Confirmed && d.ValidityWindow.Contains(today))
            .ToList();

        // DD14: pending = Pending ∧ not yet expired (today ≤ valid_to). Expired-unreviewed deals drop off.
        var pending = all
            .Where(d => d.Status == DealStatus.Pending && today <= d.ValidityWindow.ValidTo)
            .ToList();

        // Batch name resolution (no N+1): store names for both partitions; product names + categories for
        // the resolved (active) products only — a pending deal carries no committed product to resolve.
        var storeNames = await stores.ResolveNamesAsync(
            active.Concat(pending).Select(d => d.StoreId).Distinct().ToList(), ct);

        var productIds = active
            .Where(d => d.ProductId is not null)
            .Select(d => d.ProductId!.Value)
            .Distinct()
            .ToList();
        var productInfos = productIds.Count == 0
            ? NoProducts
            : await products.ForProductsAsync(productIds, ct);

        var activeViews = active
            .Select(d => ToView(d, storeNames, productInfos))
            .OrderBy(v => v.StoreName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pendingViews = pending
            .Select(d => ToView(d, storeNames, productInfos))
            .OrderBy(v => v.ValidTo)
            .ThenBy(v => v.StoreName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DealsBoard(activeViews, pendingViews);
    }

    private static DealView ToView(
        Deal deal,
        IReadOnlyDictionary<Guid, string> storeNames,
        IReadOnlyDictionary<Guid, DealProductInfo> productInfos)
    {
        DealProductInfo? product = deal.ProductId is { } pid && productInfos.TryGetValue(pid, out var info)
            ? info
            : null;

        return new DealView(
            deal.Id,
            deal.StoreId,
            storeNames.TryGetValue(deal.StoreId, out var storeName) ? storeName : "(unknown store)",
            deal.ProductId,
            product?.Name,
            product?.CategoryName,
            deal.RawName,
            deal.Brand,
            deal.SaleStory,
            deal.Price,
            deal.Quantity,
            deal.ValidityWindow.ValidFrom,
            deal.ValidityWindow.ValidTo,
            deal.AutoMatched);
    }
}
