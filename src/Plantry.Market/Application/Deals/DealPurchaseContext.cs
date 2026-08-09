namespace Plantry.Market.Application;

/// <summary>
/// Purchase-history context for one pending deal's suggested product (plantry-gtgl,
/// stats-page-prototype.html appendix "Deals review" injection point) — "what do I normally pay, how often
/// do I buy it, when did I last buy it" surfaced at the moment of the confirm/reject decision, turning "is
/// this a good deal?" from a guess into a read.
///
/// <para>Built only for a deal with a resolved suggested product AND at least one live purchase/manual price
/// observation (DL-O4) — a product with no purchase history yields no context at all (the ticket's "skip the
/// row silently"), never a context with null/zero fields standing in for "unknown".</para>
/// </summary>
/// <param name="AverageUnitPrice">Mean unit price across the product's purchase/manual observation history
/// (<see cref="PriceHistoryStats.Average"/>) — the "you pay $X avg" figure.</param>
/// <param name="DealUnitPrice">The deal's own price, unit-normalized via <c>IUnitPriceCalculator</c>. Null
/// when the deal carries no usable pack size/unit (DM-17 soft-fail) — the deal is still shown, just without
/// a percent comparison.</param>
/// <param name="PercentDelta"><c>(DealUnitPrice − AverageUnitPrice) / AverageUnitPrice × 100</c>, rounded to
/// one decimal place. Negative means the deal undercuts the household's average (the "good deal" framing).
/// Null when <see cref="DealUnitPrice"/> could not be resolved.</param>
/// <param name="AveragePurchaseInterval">Mean time between consecutive purchase-journal movements
/// (<see cref="PurchaseCadence.AverageInterval"/>) — the "you buy this every ~3 weeks" figure. Null when
/// fewer than two purchase movements exist (no interval to measure).</param>
/// <param name="LastPurchasedAt">The date of the most recent purchase/manual price observation.</param>
public sealed record DealPurchaseContext(
    decimal AverageUnitPrice,
    decimal? DealUnitPrice,
    decimal? PercentDelta,
    TimeSpan? AveragePurchaseInterval,
    DateOnly LastPurchasedAt);

/// <summary>Pure helpers over ordered purchase-journal timestamps — kept separate so the cadence math is
/// trivially unit-testable without a repository (mirrors <see cref="PriceHistoryStats"/>).</summary>
public static class PurchaseCadence
{
    /// <summary>
    /// Average time between consecutive purchases: the span from the earliest to the latest movement,
    /// divided by the number of gaps between them. Null when fewer than two movements exist — one purchase
    /// has no interval to measure. Order-independent (sorts internally).
    /// </summary>
    public static TimeSpan? AverageInterval(IReadOnlyList<DateTimeOffset> purchaseDates)
    {
        if (purchaseDates.Count < 2)
            return null;

        var sorted = purchaseDates.OrderBy(d => d).ToList();
        var span = sorted[^1] - sorted[0];
        return span / (sorted.Count - 1);
    }
}
