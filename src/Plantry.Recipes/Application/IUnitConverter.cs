using Plantry.SharedKernel;

namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption port for unit conversion (recipes-domain-model.md §8; DM-12). Resolves a quantity
/// between two units <i>for a specific product</i> — same-dimension scaling plus that product's own
/// <c>ProductConversion</c> overrides — and fails loudly with a <see cref="Result{T}"/> error when no
/// path exists (never a silent identity or zero). Used by Author (authoring-time validation, C10) and,
/// from P2-2, Costing. Defined here in Recipes.Application and <b>implemented in Plantry.Web</b> over
/// Catalog's pure <c>UnitConverter</c>, so the Recipes project keeps its <c>→ SharedKernel only</c>
/// dependency. Identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface IUnitConverter
{
    Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default);
}
