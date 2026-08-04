using Plantry.SharedKernel;

namespace Plantry.Planning.Application;

/// <summary>
/// Anti-corruption port for unit conversion, owned by MealPlanning (plantry-9n7l). Mirrors
/// <c>Plantry.Recipes.Application.IUnitConverter</c> — a separate copy per context, never a shared
/// cross-context type (DM-3) — so MealPlanning stays free of a Recipes dependency. Resolves a quantity
/// between two units <i>for a specific product</i> — same-dimension scaling plus that product's own
/// <c>ProductConversion</c> overrides — and fails loudly with a <see cref="Result{T}"/> error when no
/// path exists (never a silent identity or zero).
///
/// Added so <see cref="Plantry.Planning.Domain.PlanCostingService"/> can convert a price
/// observation's unit onto a product's default unit before costing a product-dish, the same shape
/// <c>CostingService</c> already uses for recipe ingredient lines. Defined here in
/// MealPlanning.Application and implemented in Plantry.Web/Composition over Catalog's pure
/// <c>UnitConverter</c>. Identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface IMealPlanUnitConverter
{
    Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default);
}
