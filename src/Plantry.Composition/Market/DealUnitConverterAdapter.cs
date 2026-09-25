using Plantry.Market.Application;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;

namespace Plantry.Web.Market;

/// <summary>
/// Composition-level bridge from Market's <see cref="IProductUnitConverter"/> (plantry-oh27.6) onto
/// Catalog's real conversion machinery via <see cref="IUnitConverter"/> — the same wrapping
/// <see cref="PriceHistoryReaderAdapter"/> does for its own copy of this delegate, extracted here so
/// <c>ReviewDeals</c> (which lives in <c>Plantry.Market.Application</c>, not Composition) can also feed
/// <see cref="PriceHistoryRollup.ForProductsAsync"/> a real converter without depending on Catalog itself
/// (ADR-010/DM-3).
/// </summary>
public sealed class DealUnitConverterAdapter(IUnitConverter converter) : IProductUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
        converter.ConvertAsync(productId, amount, fromUnitId, toUnitId, ct);
}
