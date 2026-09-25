using Plantry.Market.Application;
using Plantry.SharedKernel;

namespace Plantry.Web.Market;

/// <summary>
/// Composition-level helper over <see cref="PriceHistoryRollup"/> (plantry-oh27.5) — resolves the
/// depth-1 parent/variant catalog context via <see cref="ICatalogProductReader"/> (the same port
/// <c>PriceReaderAdapter</c>/<c>MealPlanPriceReaderAdapter</c> already use for the sibling
/// <see cref="EffectivePriceRollup"/>) so a Web-layer consumer never builds a <see cref="PriceRollupProduct"/>
/// by hand. No production consumer yet — the parent product detail page and deals-review purchase-context
/// beads wire this in.
/// </summary>
public sealed class PriceHistoryReaderAdapter(
    PricingQueries pricingQueries,
    Plantry.Recipes.Application.ICatalogProductReader catalog,
    Plantry.Recipes.Application.IUnitConverter converter)
{
    public async Task<RolledUpPriceHistory> ForProductAsync(Guid productId, CancellationToken ct = default)
    {
        var found = await catalog.FindManyWithVariantsAsync([productId], ct);
        found.TryGetValue(productId, out var product);
        var context = PriceRollupContextBuilder.Build(productId, product);
        return await PriceHistoryRollup.ForProductAsync(pricingQueries, context, ConvertAsync, ct);
    }

    private Task<Result<decimal>> ConvertAsync(Guid productId, decimal amount, Guid from, Guid to, CancellationToken ct) =>
        converter.ConvertAsync(productId, amount, from, to, ct);
}
