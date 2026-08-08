using Plantry.Pantry.Domain;
using Plantry.Market.Application;

namespace Plantry.Web.Pricing;

/// <summary>
/// Web-side adapter for the unit-price normalization seam. Lives in Plantry.Web — the
/// composition root that already references Catalog — so Plantry.Market has no dependency
/// on Catalog (mirrors the CatalogConversionProvider seam for Inventory).
///
/// Normalizes: unit_price = price / (quantity × factorToBase), giving price per base unit
/// of the dimension (e.g. per gram, per ml). Returns null on any resolution failure (soft-fail).
///
/// <para>Unit reads are memoized per request (the adapter is registered scoped): a receipt review
/// normalizes every line's own price in a loop, and without the cache each call would issue its own
/// <c>units</c> SELECT — the same per-request-cache-over-<see cref="IUnitRepository"/> precedent
/// <c>UnitCodesAccessor</c> established for the identical symptom (plantry-47tc/plantry-hw39).
/// Negative results are cached too — a missing unit stays missing for the request.</para>
/// </summary>
public sealed class UnitPriceCalculatorAdapter(IUnitRepository units) : IUnitPriceCalculator
{
    private readonly Dictionary<Guid, Unit?> _units = [];

    public async Task<decimal?> TryNormalizeAsync(
        decimal price, decimal quantity, Guid unitId, CancellationToken ct = default)
    {
        if (!_units.TryGetValue(unitId, out var unit))
            _units[unitId] = unit = await units.FindAsync(UnitId.From(unitId), ct);
        if (unit is null || quantity <= 0m || unit.FactorToBase <= 0m)
            return null;

        return price / (quantity * unit.FactorToBase);
    }
}
