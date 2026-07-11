using System.Globalization;
using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// The one place Recipes' quantity-display need (Details, Cook) meets Catalog's pure
/// <see cref="QuantityDisplay"/> formatter (quantity-display.md Q8). Pure and side-effect free: the
/// household's units are supplied by the caller (the live adapter loads them once; the L4 fakes pass a
/// fixed set), exactly as <see cref="QuantityDisplay"/> mandates. Kept static and shared so the live
/// <see cref="RecipesQuantityFormatterAdapter"/> and the test fake format through identical logic.
/// </summary>
public static class QuantityFormatting
{
    /// <summary>
    /// Formats each <paramref name="requests"/> entry against the household's <paramref name="units"/>.
    /// For a <see cref="QuantityFormatRequest.Simplify"/> request the amount is first run through
    /// <see cref="QuantityDisplay.Simplify"/> (Q2), then rendered with <see cref="QuantityDisplay.FormatAmount"/>
    /// in the resulting unit's <see cref="Unit.DisplayStyle"/> (Q1). A request whose unit is unknown to
    /// the household falls back to the historical <c>0.###</c> decimal in the authored unit.
    /// </summary>
    public static IReadOnlyDictionary<string, FormattedQuantity> Format(
        IReadOnlyList<QuantityFormatRequest> requests, IReadOnlyList<Unit> units)
    {
        var byId = units.ToDictionary(u => u.Id.Value);
        var result = new Dictionary<string, FormattedQuantity>();

        foreach (var request in requests)
        {
            if (!byId.TryGetValue(request.UnitId, out _))
            {
                // Unit not known to this household — render the historical decimal, unit unchanged.
                result[request.Key] = new FormattedQuantity(
                    request.Amount.ToString("0.###", CultureInfo.InvariantCulture), request.UnitId);
                continue;
            }

            var (amount, unitId) = request.Simplify
                ? QuantityDisplay.Simplify(request.Amount, request.UnitId, units)
                : (request.Amount, request.UnitId);

            // Simplify only ever returns the authored unit or a household sibling, so this lookup holds.
            var displayUnit = byId[unitId];
            var formatted = QuantityDisplay.FormatAmount(amount, displayUnit.DisplayStyle);
            result[request.Key] = new FormattedQuantity(formatted, unitId);
        }

        return result;
    }
}
