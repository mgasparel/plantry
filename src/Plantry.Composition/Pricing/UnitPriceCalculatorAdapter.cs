using Plantry.Catalog.Domain;
using Plantry.Pricing.Application;

namespace Plantry.Web.Pricing;

/// <summary>
/// Web-side adapter for the unit-price normalization seam. Lives in Plantry.Web — the
/// composition root that already references Catalog — so Plantry.Pricing has no dependency
/// on Catalog (mirrors the CatalogConversionProvider seam for Inventory).
///
/// Normalizes: unit_price = price / (quantity × factorToBase), giving price per base unit
/// of the dimension (e.g. per gram, per ml). Returns null on any resolution failure (soft-fail).
/// </summary>
public sealed class UnitPriceCalculatorAdapter(IUnitRepository units) : IUnitPriceCalculator
{
    public async Task<decimal?> TryNormalizeAsync(
        decimal price, decimal quantity, Guid unitId, CancellationToken ct = default)
    {
        var unit = await units.FindAsync(UnitId.From(unitId), ct);
        if (unit is null || quantity <= 0m || unit.FactorToBase <= 0m)
            return null;

        return price / (quantity * unit.FactorToBase);
    }
}
