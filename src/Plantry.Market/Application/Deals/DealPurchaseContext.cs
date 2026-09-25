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
/// (<see cref="PriceHistoryStats.Average"/>) — the "you pay $X avg" figure. Its basis (what "per 1 unit"
/// means) is <see cref="AverageBasisUnitId"/>: null means the historical per-BASE-unit contract (a leaf's
/// <c>IUnitPriceCalculator</c>-normalized price, DM-17); a non-null id (plantry-oh27.6, a parent suggestion)
/// means per 1 <see cref="AverageBasisUnitId"/> instead — <b>never</b> re-scale this value by a unit's
/// <c>FactorToBase</c> when <see cref="AverageBasisUnitId"/> is set, or it double-converts.</param>
/// <param name="DealUnitPrice">The deal's own price, normalized onto the <b>same basis</b> as
/// <see cref="AverageUnitPrice"/> — via <c>IUnitPriceCalculator</c> (per-base-unit) when
/// <see cref="AverageBasisUnitId"/> is null, or via a direct unit conversion into
/// <see cref="AverageBasisUnitId"/> when it is set — so the two are always comparable. Null when the deal
/// carries no usable pack size/unit (DM-17 soft-fail) or no conversion path exists — the deal is still
/// shown, just without a percent comparison.</param>
/// <param name="PercentDelta"><c>(DealUnitPrice − AverageUnitPrice) / AverageUnitPrice × 100</c>, rounded to
/// one decimal place. Negative means the deal undercuts the household's average (the "good deal" framing).
/// Null when <see cref="DealUnitPrice"/> could not be resolved.</param>
/// <param name="AveragePurchaseInterval">Mean time between consecutive purchase-journal movements
/// (<see cref="PurchaseCadence.AverageInterval"/>) — the "you buy this every ~3 weeks" figure. Null when
/// fewer than two purchase movements exist (no interval to measure).</param>
/// <param name="LastPurchasedAt">The date of the most recent purchase/manual price observation.</param>
/// <param name="AverageBasisUnitId">Null for a leaf suggestion (the historical per-BASE-unit contract every
/// consumer up to plantry-gtgl relied on). Set to the parent's <c>DefaultUnitId</c>
/// (<c>RolledUpPriceHistory.ReferenceUnitId</c>) for a parent suggestion (plantry-oh27.6) —
/// <see cref="PriceHistoryRollup"/> expresses <see cref="AverageUnitPrice"/> per 1 of the parent's own
/// default unit, not per base unit, so a consumer must skip any further <c>FactorToBase</c> scaling
/// (<c>_DealReviewCard.cshtml</c>'s "You pay" line) when this is set.</param>
public sealed record DealPurchaseContext(
    decimal AverageUnitPrice,
    decimal? DealUnitPrice,
    decimal? PercentDelta,
    TimeSpan? AveragePurchaseInterval,
    DateOnly LastPurchasedAt,
    Guid? AverageBasisUnitId = null);

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
