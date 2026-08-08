namespace Plantry.Market.Application;

/// <summary>
/// One point in a product's price-history trend (plantry-fuej, stats-page-prototype.html appendix "Catalog
/// / Pantry product detail" injection point). <see cref="UnitPrice"/> is the value already unit-normalized
/// by <c>IUnitPriceCalculator</c> at record time, so points recorded from differing pack sizes are directly
/// comparable — the same "confidently unit-normalizable" gate plantry-bb7p's price-delta chips use.
/// </summary>
public sealed record PriceHistoryPoint(DateOnly ObservedAt, decimal UnitPrice);

/// <summary>
/// Pure statistics over an ordered <see cref="PriceHistoryPoint"/> series — kept separate from
/// <see cref="PricingQueries"/> so the median computation is trivially unit-testable without a repository.
/// </summary>
public static class PriceHistoryStats
{
    /// <summary>The product's median unit price across its history — the "you pay X median" stat. Null
    /// when there are no usable points; the caller decides the minimum count worth displaying.</summary>
    public static decimal? Median(IReadOnlyList<PriceHistoryPoint> points)
    {
        if (points.Count == 0)
            return null;

        var sorted = points.Select(p => p.UnitPrice).OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2m
            : sorted[mid];
    }
}
