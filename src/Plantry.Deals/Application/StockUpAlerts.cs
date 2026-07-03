using Plantry.Deals.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Application;

/// <summary>
/// One stock-up alert (DJ5 / §7): a product the household <b>frequently buys</b> that <b>currently has an
/// active deal</b>. Carries the resolved product, the <b>cheapest</b> active deal's store + price and the
/// deal's validity window, plus the trailing-window purchase count that qualified it. Read-time only —
/// nothing here is stored.
/// </summary>
public sealed record StockUpAlert(
    Guid ProductId,
    string ProductName,
    DealId DealId,
    Guid StoreId,
    string StoreName,
    decimal Price,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    int PurchaseCount);

/// <summary>
/// <c>StockUpAlerts</c> read model (DJ5 / §7). Intersects the household's <b>frequently-bought</b> products
/// (from Inventory's purchase journal, via <see cref="IPurchaseFrequencyReader"/>, DL-O4) with its
/// <b>active deals</b> (Deals' own in-context <see cref="BrowseDeals"/> active partition, P5-7 — <b>not</b>
/// any exposed Deals reader or the Shopping badge, per ADR-010). A frequent product with no active deal
/// yields no alert, and an active deal on a product bought below the frequency threshold yields no alert.
///
/// <para><b>All read-time, nothing stored (D11).</b> Both the frequency window and the active-deal window
/// are recomputed on demand against <see cref="IClock"/>, so an alert appears/vanishes as the clock moves,
/// a deal lapses, or purchase history ages out — no "is alertable" flag is ever persisted.</para>
///
/// <para><b>Frequency heuristic (locked, tunable):</b> a product is "frequently bought" when it has
/// <see cref="FrequencyThreshold"/> or more purchase movements within the trailing
/// <see cref="FrequencyWindowDays"/> days. This is a heuristic, not a spec'd value — kept as two named
/// constants in one place so it is cheap to tune against real data.</para>
/// </summary>
public sealed class StockUpAlerts(
    IPurchaseFrequencyReader purchaseFrequency,
    IClock clock)
{
    /// <summary>Minimum purchase movements within the window to count as "frequently bought" (heuristic).</summary>
    public const int FrequencyThreshold = 3;

    /// <summary>Trailing window, in days, over which purchases are counted (heuristic; user-locked at 120d).</summary>
    public const int FrequencyWindowDays = 120;

    /// <summary>
    /// Computes the household's current stock-up alerts from an already-computed set of <b>active deals</b>
    /// (Deals' own P5-7 active partition, e.g. <c>DealsBoard.Active</c>) — the caller owns the single
    /// <see cref="BrowseDeals"/> read so the Deals page does not query the deal repo twice per request. For
    /// each frequently-bought product that has at least one active deal, emits one alert carrying the
    /// <b>cheapest</b> active deal (ties broken by the soonest end date, then deal id, for a deterministic
    /// pick). Ordered by product name (A→Z).
    /// </summary>
    public async Task<IReadOnlyList<StockUpAlert>> ComputeAsync(
        IReadOnlyList<DealView> activeDeals,
        CancellationToken ct = default)
    {
        // Active deals from Deals' own data (P5-7 active partition), grouped by their resolved product.
        var dealsByProduct = activeDeals
            .Where(d => d.ProductId is not null)
            .GroupBy(d => d.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (dealsByProduct.Count == 0)
            return [];

        // Trailing purchase-frequency window as an absolute instant (the port carries no clock).
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var windowStart = today.AddDays(-FrequencyWindowDays);
        var since = new DateTimeOffset(windowStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var purchaseCounts = await purchaseFrequency.PurchaseCountsSinceAsync(since, ct);

        var alerts = new List<StockUpAlert>();
        foreach (var (productId, deals) in dealsByProduct)
        {
            // Intersection: keep only products that are BOTH on an active deal AND frequently bought.
            if (!purchaseCounts.TryGetValue(productId, out var count) || count < FrequencyThreshold)
                continue;

            // Cheapest active deal for this product; deterministic tie-break.
            var cheapest = deals
                .OrderBy(d => d.Price)
                .ThenBy(d => d.ValidTo)
                .ThenBy(d => d.DealId.Value)
                .First();

            alerts.Add(new StockUpAlert(
                productId,
                cheapest.DisplayName,
                cheapest.DealId,
                cheapest.StoreId,
                cheapest.StoreName,
                cheapest.Price,
                cheapest.ValidFrom,
                cheapest.ValidTo,
                count));
        }

        return alerts
            .OrderBy(a => a.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
