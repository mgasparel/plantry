using Plantry.Pantry.Domain;
using Plantry.SharedKernel;

namespace Plantry.Web.Housekeeping;

// ── Shared fact records (ADR-021) ───────────────────────────────────────────────────────────────
//
// These records are the flat, cross-schema shapes both Tidy Up read models (StockFactsReadModel,
// RecipeFactsReadModel) load their SQL results into. Shared here rather than duplicated per-bag so
// the two bags' Products/Units/ConversionsByProduct dictionaries carry identical semantics and both
// can hand the same conversion-shape mapping to the canonical Plantry.Pantry.Domain.UnitConverter
// (mirrors Plantry.Web.MealPlanning.WeekBagEnricher.BuildConverter, plantry-jvd7).

/// <summary>Product facts from <c>catalog.products</c> — only active (non-archived) products, matching the
/// pre-conversion <c>ICatalogReadFacade.ListProductsAsync</c>/<c>ICatalogProductReader.FindManyAsync</c>
/// convention every detector's "product archived/removed from catalog — skip" comment relies on.</summary>
public sealed record ProductFact(
    Guid ProductId,
    string Name,
    bool TrackStock,
    Guid DefaultUnitId,
    Guid? DefaultLocationId = null,
    bool IsParent = false);

/// <summary>Unit display facts from <c>catalog.units</c>.</summary>
public sealed record UnitFact(
    Guid UnitId,
    string Code,
    string Name,
    string Dimension,
    decimal? FactorToBase,
    bool IsBase);

/// <summary>One unit conversion from <c>catalog.product_conversions</c>.</summary>
public sealed record ConversionFact(
    Guid ProductId,
    Guid FromUnitId,
    Guid ToUnitId,
    decimal Factor);

/// <summary>
/// Builds the shared conversion delegate both bags hand their detectors — maps flat <see cref="UnitFact"/>/
/// <see cref="ConversionFact"/> rows onto <see cref="UnitConverter"/>'s shape-typed overload (plantry-jvd7),
/// so Tidy Up's conversion checks run the exact same graph algorithm Recipes/Inventory/Meal Planning do,
/// entirely over already-loaded data (ADR-021 rule 1 — no round-trips inside the returned delegate).
/// </summary>
internal static class HousekeepingConversions
{
    public static Func<Guid, decimal, Guid, Guid, Result<decimal>> BuildConverter(
        IReadOnlyDictionary<Guid, UnitFact> units,
        IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> conversionsByProduct)
    {
        var unitShapes = units.Values
            .Select(u => new UnitConverter.UnitShape(u.UnitId, DimensionExtensions.Parse(u.Dimension), u.FactorToBase ?? 1m))
            .ToList();

        return (productId, amount, fromUnitId, toUnitId) =>
        {
            var conversionShapes = (conversionsByProduct.TryGetValue(productId, out var list) ? list : [])
                .Select(c => new UnitConverter.ConversionShape(c.FromUnitId, c.ToUnitId, c.Factor))
                .ToList();

            return UnitConverter.Convert(amount, fromUnitId, toUnitId, unitShapes, conversionShapes);
        };
    }
}
