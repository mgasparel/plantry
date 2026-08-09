using Plantry.Intake.Application;
using Plantry.Intake.Domain;

namespace Plantry.Web.Pages.Intake;

/// <summary>
/// Combines each Confirmed review line's own unit price with its product's last purchase unit price into
/// a percentage delta (e.g. <c>0.12m</c> for "▲ 12% vs last time", <c>-0.08m</c> for "▼ 8%") — the
/// per-line price-delta stat from the Intake review injection point (stats-page-prototype.html appendix,
/// plantry-bb7p).
///
/// <para>Pure over pre-fetched values: it combines Intake line data with Market pricing data that the
/// caller (<c>Review.cshtml.cs</c>) has already resolved through <c>IPriceObservationRepository</c> and
/// <c>IUnitPriceCalculator</c> — the same reason <see cref="IntakeReviewHydrationBuilder"/> stays in
/// Plantry.Web rather than Plantry.Intake (bounded-context discipline, ADR-010): this glue is cross-context
/// by nature and Plantry.Intake.Application may not reference Plantry.Market types.</para>
///
/// <para>Only a Confirmed line is considered — a still-Pending line has no committed price to compare, and
/// Dismissed/Committed lines never reach this shape from the review page. A line is included only when
/// BOTH its own current unit price and a prior purchase unit price for the product are present in the
/// caller-supplied maps — the issue's "only when confidently unit-normalizable" gate. A soft-failed
/// normalization on either side (the caller never adds the entry) silently omits the line rather than
/// showing a misleading number.</para>
/// </summary>
public static class IntakeLinePriceDeltas
{
    public static IReadOnlyDictionary<Guid, decimal> Compute(
        IEnumerable<ReviewLineView> lines,
        IReadOnlyDictionary<Guid, decimal> currentUnitPriceByLineId,
        IReadOnlyDictionary<Guid, decimal> lastUnitPriceByProductId)
    {
        var result = new Dictionary<Guid, decimal>();
        foreach (var line in lines)
        {
            if (line.Status != LineStatus.Confirmed || line.ProductId is not { } productId)
                continue;
            if (!currentUnitPriceByLineId.TryGetValue(line.LineId, out var current))
                continue;
            if (!lastUnitPriceByProductId.TryGetValue(productId, out var last) || last <= 0m)
                continue;

            result[line.LineId] = (current - last) / last;
        }
        return result;
    }
}
