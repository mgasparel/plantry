using Plantry.Pantry.Domain;

namespace Plantry.Pantry.Application;

/// <summary>
/// Hands <see cref="ProductStock.Consume"/> a unit converter built for a specific product (its
/// household's units + that product's conversion overrides). Its adapter composes Catalog's
/// <c>UnitConverter</c> directly — an intra-context Pantry collaboration now that Catalog and
/// Inventory live in one assembly (ADR-024, plantry-g3da.6).
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
