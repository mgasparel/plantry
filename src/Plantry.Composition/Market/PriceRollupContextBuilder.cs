using Plantry.Market.Application;
using Plantry.Recipes.Application;

namespace Plantry.Web.Market;

/// <summary>
/// Builds a <see cref="PriceRollupProduct"/> rollup context from an <see cref="ICatalogProductReader"/>
/// lookup (plantry-oh27.5) — the depth-1 parent/variant tree translated into the shape
/// <see cref="EffectivePriceRollup"/>/<see cref="PriceHistoryRollup"/> select over. Extracted out of
/// <c>PriceReaderAdapter</c> and <c>MealPlanPriceReaderAdapter</c>, which previously each carried their
/// own verbatim copy of this exact translation (plantry-i07l), so a third rollup consumer
/// (<c>PriceHistoryReaderAdapter</c>) does not add a fourth.
/// </summary>
public static class PriceRollupContextBuilder
{
    /// <summary>
    /// A product absent from the catalog is treated as a concrete leaf (self) with an unknown default
    /// unit — pre-DM-19 behaviour every rollup consumer relies on (a leaf needs no catalog round-trip to
    /// price). A parent keeps each live variant as its own rollup variant, populated with the variant's
    /// own default unit (falling back to the parent's when a per-variant unit was not resolved).
    /// </summary>
    public static PriceRollupProduct Build(Guid productId, CatalogProduct? product)
    {
        if (product is null)
            return new PriceRollupProduct(productId, Guid.Empty, IsParent: false, []);

        return new PriceRollupProduct(product.Id, product.DefaultUnitId, product.IsParent,
            product.VariantProductIds.Select(id => new PriceRollupVariant(id,
                product.VariantDefaultUnitIds?.GetValueOrDefault(id) ?? product.DefaultUnitId)).ToList());
    }
}
