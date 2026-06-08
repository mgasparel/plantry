using Plantry.SharedKernel;

namespace Plantry.Catalog.Domain;

/// <summary>
/// Resolves a quantity from one unit to another (DM-12). Pure and side-effect free — callers
/// supply the household's units and the product's conversions; nothing is loaded here.
///
/// Resolution order:
///   1. Same unit / same dimension → linear scaling via <see cref="Unit.FactorToBase"/>
///      (factor 1 for the same unit).
///   2. Product-specific <see cref="ProductConversion"/> (cross-dimension / density), tried in
///      its stored direction and then its inverse — each bridged on either side by a
///      same-dimension hop when the caller's units don't exactly match the conversion's anchors
///      (e.g. resolving tablespoons against a product conversion anchored on cups).
///   3. Fail loudly — never silently return an identity or zero result.
///
/// Unit IDs are raw <see cref="Guid"/>s (not the strongly-typed <see cref="UnitId"/>) so this
/// service can be driven directly from cross-context values such as <see cref="Quantity"/>.
/// </summary>
public static class UnitConverter
{
    public static Result<decimal> Convert(
        decimal amount,
        Guid fromUnitId,
        Guid toUnitId,
        IReadOnlyCollection<Unit> units,
        IReadOnlyCollection<ProductConversion> productConversions)
    {
        if (SameDimensionFactor(fromUnitId, toUnitId, units) is { } factor)
            return amount * factor;

        foreach (var conversion in productConversions)
        {
            if (SameDimensionFactor(fromUnitId, conversion.FromUnitId.Value, units) is { } bridgeIn
                && SameDimensionFactor(conversion.ToUnitId.Value, toUnitId, units) is { } bridgeOut)
            {
                return amount * bridgeIn * conversion.Factor * bridgeOut;
            }
        }

        foreach (var conversion in productConversions)
        {
            if (SameDimensionFactor(fromUnitId, conversion.ToUnitId.Value, units) is { } bridgeIn
                && SameDimensionFactor(conversion.FromUnitId.Value, toUnitId, units) is { } bridgeOut)
            {
                return amount * bridgeIn / conversion.Factor * bridgeOut;
            }
        }

        return Error.Custom("Catalog.UnresolvableConversion",
            $"No conversion is known from unit '{fromUnitId}' to unit '{toUnitId}'.");
    }

    /// <summary>Linear scaling factor between two units of the same dimension — 1 for the same unit, null when unresolvable.</summary>
    private static decimal? SameDimensionFactor(Guid fromUnitId, Guid toUnitId, IReadOnlyCollection<Unit> units)
    {
        if (fromUnitId == toUnitId)
            return 1m;

        var fromUnit = units.SingleOrDefault(u => u.Id.Value == fromUnitId);
        var toUnit = units.SingleOrDefault(u => u.Id.Value == toUnitId);

        if (fromUnit is not null && toUnit is not null && fromUnit.Dimension == toUnit.Dimension)
            return fromUnit.FactorToBase / toUnit.FactorToBase;

        return null;
    }
}
