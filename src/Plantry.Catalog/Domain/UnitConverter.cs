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

    /// <summary>
    /// Enumerates every unit the caller can express a quantity in for the given product, ordered
    /// with <paramref name="defaultUnitId"/> first and then alphabetically by code.
    ///
    /// A unit is "reachable" if:
    ///   (a) it is the default unit, OR
    ///   (b) it shares the same <see cref="Unit.Dimension"/> as the default unit (same-dimension
    ///       siblings — no <see cref="ProductConversion"/> required), OR
    ///   (c) it is the <c>FromUnit</c> or <c>ToUnit</c> of any <see cref="ProductConversion"/>
    ///       on the product, OR
    ///   (d) it shares the same <see cref="Unit.Dimension"/> as any such conversion anchor
    ///       (bridged siblings — reached via a same-dimension hop on either side).
    ///
    /// A product with no conversions returns a single-element list (its default unit only).
    /// </summary>
    public static IReadOnlyList<Guid> ReachableUnits(
        Guid defaultUnitId,
        IReadOnlyList<Unit> allUnits,
        IReadOnlyList<ProductConversion> productConversions)
    {
        // Index units for fast lookup.
        var unitById = allUnits.ToDictionary(u => u.Id.Value);

        // Collect the dimensions that are reachable.
        var reachableDimensions = new HashSet<Dimension>();

        // (a)+(b) default unit and its dimension.
        if (unitById.TryGetValue(defaultUnitId, out var defaultUnit))
            reachableDimensions.Add(defaultUnit.Dimension);

        // (c)+(d) each conversion anchor and its dimension siblings.
        foreach (var conv in productConversions)
        {
            if (unitById.TryGetValue(conv.FromUnitId.Value, out var fromUnit))
                reachableDimensions.Add(fromUnit.Dimension);
            if (unitById.TryGetValue(conv.ToUnitId.Value, out var toUnit))
                reachableDimensions.Add(toUnit.Dimension);
        }

        // Every unit whose dimension is reachable is reachable.
        var reachable = allUnits
            .Where(u => reachableDimensions.Contains(u.Dimension))
            .Select(u => u.Id.Value)
            .ToHashSet();

        // Always include the default unit (even if it somehow has no dimension entry).
        reachable.Add(defaultUnitId);

        // Order: default first, then by code ascending.
        var ordered = reachable
            .OrderBy(id => id == defaultUnitId ? 0 : 1)
            .ThenBy(id => unitById.TryGetValue(id, out var u) ? u.Code : string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ordered;
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
