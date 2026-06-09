using Plantry.Inventory.Domain;

namespace Plantry.Inventory.Application;

/// <summary>
/// The cross-context seam that hands <see cref="ProductStock.Consume"/> a unit converter built for a
/// specific product (its household's units + that product's conversion overrides). Defined in
/// Inventory.Application; <b>implemented in Plantry.Web</b> over Catalog's <c>UnitConverter</c>
/// (PHASE-1-PLAN.md §dependency rules — the ID-only application-interface seam). This is what lets
/// the Inventory project stay <c>→ SharedKernel only</c> while still converting across units.
/// </summary>
public interface IProductConversionProvider
{
    Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Batch-loads converters for multiple products. Implementations should override this to avoid N+1 queries.</summary>
    async Task<IReadOnlyDictionary<Guid, IQuantityConverter>> ForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IQuantityConverter>();
        foreach (var id in productIds)
            result[id] = await ForProductAsync(id, ct);
        return result;
    }
}
