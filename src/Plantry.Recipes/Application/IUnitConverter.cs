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

    /// <summary>
    /// Batch form of the "does a path exist" check behind <see cref="ConvertAsync"/> — for a caller
    /// (e.g. a Housekeeping detector, plantry-4t0g) that needs to check many (product, from-unit,
    /// to-unit) triples in one pass instead of one <see cref="ConvertAsync"/> round trip per line.
    /// Returns the subset of <paramref name="triples"/> that have <b>no</b> conversion path. The default
    /// implementation dedupes and falls back to one <see cref="ConvertAsync"/> call per distinct triple;
    /// implementations should override to pre-load their backing state once per batch (mirrors
    /// <c>IProductConversionProvider.ForProductsAsync</c>).
    /// </summary>
    async Task<IReadOnlySet<(Guid ProductId, Guid FromUnitId, Guid ToUnitId)>> FindUnconvertiblePathsAsync(
        IEnumerable<(Guid ProductId, Guid FromUnitId, Guid ToUnitId)> triples, CancellationToken ct = default)
    {
        var unconvertible = new HashSet<(Guid ProductId, Guid FromUnitId, Guid ToUnitId)>();
        foreach (var triple in triples.Distinct())
        {
            var result = await ConvertAsync(triple.ProductId, 1m, triple.FromUnitId, triple.ToUnitId, ct);
            if (result.IsFailure)
                unconvertible.Add(triple);
        }
        return unconvertible;
    }
}
