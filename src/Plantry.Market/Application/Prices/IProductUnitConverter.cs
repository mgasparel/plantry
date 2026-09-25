using Plantry.SharedKernel;

namespace Plantry.Market.Application;

/// <summary>
/// Anti-corruption port for unit conversion (plantry-oh27.6), the Market twin of
/// <c>Plantry.Recipes.Application.IUnitConverter</c> (same shape, deliberately) — resolves a quantity
/// between two units <i>for a specific product</i> and fails loudly with a <see cref="Result{T}"/> error
/// when no path exists, never a silent identity or zero. Defined here in <c>Plantry.Market.Application</c>
/// and <b>implemented in Plantry.Web/Composition</b> over Catalog's real conversion machinery, so
/// <c>Plantry.Market</c> stays free of any Catalog dependency (ADR-010/DM-3) — the same per-context port
/// shape as <see cref="ICatalogProductReader"/> here. Feeds the <c>convert</c> delegate
/// <see cref="EffectivePriceRollup"/>/<see cref="PriceHistoryRollup"/> already accept, letting
/// <c>ReviewDeals</c> — which lives in this project, not Composition — call
/// <see cref="PriceHistoryRollup.ForProductsAsync"/> directly for a parent-matched deal's purchase context
/// without itself depending on Catalog.
/// </summary>
public interface IProductUnitConverter
{
    Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default);
}
