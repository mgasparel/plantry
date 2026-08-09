using Plantry.Planning.Application;
using Plantry.SharedKernel.Domain;

namespace Plantry.Planning.Domain;

/// <summary>
/// Domain service that estimates the shopping list's basket cost (plantry-e016, stats-injection appendix:
/// ".preview/stats-page-prototype.html" §"Shopping list — Estimated basket cost"). Stateless; reads Pricing's
/// deal-aware effective price per product via <see cref="IShoppingPriceReader"/> (Shopping→Pricing ACL, P5-9
/// sibling) and converts the observation's unit onto the item's own unit via
/// <see cref="IShoppingCatalogReader.TryConvertAsync"/> — the same Shopping→Catalog conversion port the
/// add-item/recategorize flows already use. Mirrors <see cref="PlanCostingService"/>'s per-line pricing shape
/// (deal-aware price, Price/Quantity unit-price basis, unit conversion before multiplying by quantity) but is
/// Shopping-owned, a separate copy per DM-3 (no shared cross-context type).
///
/// <para>Per-line pricing rules:</para>
/// <list type="bullet">
///   <item><description><b>No price observation at all</b> (free-text item, or a product never
///     purchased/deal'd, or a degenerate zero/negative-quantity observation) — contributes nothing;
///     counted in <see cref="BasketCostEstimate.UnpricedCount"/>. Never guessed (plantry-e016 acceptance
///     criteria: "items with no price history contribute nothing and are footnoted").</description></item>
///   <item><description><b>Priced, quantity and unit both known, and the observation's unit converts onto
///     the item's unit</b> — an exact line cost, contributing equally to <see cref="BasketCostEstimate.Low"/>
///     and <see cref="BasketCostEstimate.High"/>.</description></item>
///   <item><description><b>Priced, but the item's quantity is unset or the observation's unit has no
///     conversion path onto the item's unit</b> — genuinely uncertain how much this line will cost (unlike
///     the no-observation case, a real price exists). Contributes nothing to <see cref="BasketCostEstimate.Low"/>
///     (best case) and the observation's own recorded pack price to <see cref="BasketCostEstimate.High"/> (at
///     least one pack) — never a fabricated multiplier. This is what turns the total into a range
///     (plantry-e016: "show a running estimated total, as a range when quantities/prices are
///     uncertain").</description></item>
/// </list>
///
/// Never persisted — recomputed fresh at read time (mirrors <see cref="PlanCostingService"/> / Recipes'
/// <c>CostingService</c>, J3).
/// </summary>
public sealed class ShoppingBasketCostingService(
    IShoppingPriceReader priceReader,
    IShoppingCatalogReader catalog,
    IClock clock)
{
    /// <summary>
    /// Estimates the basket cost across <paramref name="items"/> — callers pass the list's UNCHECKED items
    /// only (the outstanding basket you still need to buy; checked items are already bought and excluded
    /// from the estimate, mirroring the "before the store" framing of the injection point).
    /// </summary>
    public async Task<BasketCostEstimate> EstimateAsync(
        IReadOnlyList<ShoppingListItem> items, CancellationToken ct = default)
    {
        var productIds = items
            .Where(i => i.ProductId.HasValue)
            .Select(i => i.ProductId!.Value)
            .Distinct()
            .ToList();

        if (productIds.Count == 0)
            return BasketCostEstimate.Unpriced(items.Count);

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var prices = await priceReader.GetEffectivePricesAsync(productIds, today, ct);

        var confidentSum = 0m;
        var uncertainPackSum = 0m;
        var unpricedCount = 0;

        foreach (var item in items)
        {
            if (item.ProductId is not { } productId
                || !prices.TryGetValue(productId, out var estimate)
                || estimate.Quantity <= 0m) // degenerate observation — never fabricate a number
            {
                unpricedCount++;
                continue;
            }

            // Per one estimate.UnitId — deliberately Price/Quantity, not a pre-normalized unit price
            // (plantry-1oca basis; see ShoppingPriceEstimate's doc).
            var unitPrice = estimate.Price / estimate.Quantity;

            if (item.Quantity is not { } quantity || quantity <= 0m || item.UnitId is not { } unitId)
            {
                // Quantity/unit unspecified — genuinely uncertain how many they need. High-bound only.
                uncertainPackSum += estimate.Price;
                continue;
            }

            decimal costPerItemUnit;
            if (unitId == estimate.UnitId)
            {
                costPerItemUnit = unitPrice;
            }
            else
            {
                var converted = await catalog.TryConvertAsync(1m, estimate.UnitId, unitId, productId, ct);
                if (converted is not { } convertedAmount || convertedAmount <= 0m)
                {
                    // No conversion path — the price exists but can't be expressed in the item's unit.
                    // Treat like a quantity-uncertain line: contributes to the HIGH bound only.
                    uncertainPackSum += estimate.Price;
                    continue;
                }
                costPerItemUnit = unitPrice / convertedAmount;
            }

            confidentSum += costPerItemUnit * quantity;
        }

        if (confidentSum <= 0m && uncertainPackSum <= 0m)
            return BasketCostEstimate.Unpriced(unpricedCount);

        return new BasketCostEstimate(confidentSum, confidentSum + uncertainPackSum, unpricedCount);
    }
}

/// <summary>
/// The shopping basket cost estimate (computed read model — never persisted, plantry-e016).
/// <see cref="Low"/>/<see cref="High"/> are both null when nothing on the list could be priced at all —
/// never shown as a fabricated $0 (mirrors <c>CostCompleteness.None</c>'s null-Amount convention). When
/// every priced line was exact, <see cref="Low"/> == <see cref="High"/> and the UI renders a single figure,
/// not a range.
/// </summary>
/// <param name="Low">Sum of exact line costs only — the confident floor. Null when nothing was priced.</param>
/// <param name="High"><see cref="Low"/> plus each quantity/unit-uncertain line's own recorded pack price
/// (at least one pack). Equal to <see cref="Low"/> when there were no uncertain lines.</param>
/// <param name="UnpricedCount">Items (free-text or product-backed) with no price history at all — excluded
/// from both bounds and surfaced as a footnote, never guessed.</param>
public sealed record BasketCostEstimate(decimal? Low, decimal? High, int UnpricedCount)
{
    /// <summary>True when at least one item on the list could be priced.</summary>
    public bool HasEstimate => Low.HasValue;

    /// <summary>True when the estimate is a genuine range (some lines were quantity/unit-uncertain).</summary>
    public bool IsRange => HasEstimate && High!.Value > Low!.Value;

    /// <summary>True when at least one item has no price history to footnote.</summary>
    public bool HasUnpriced => UnpricedCount > 0;

    /// <summary>Sentinel for "nothing on the list could be priced" — still reports the unpriced count.</summary>
    public static BasketCostEstimate Unpriced(int unpricedCount) => new(null, null, unpricedCount);
}
