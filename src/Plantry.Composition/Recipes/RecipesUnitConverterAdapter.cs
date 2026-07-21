using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IUnitConverter"/> — the one place Recipes' (and, from P2-2, Costing's)
/// conversion need meets Catalog's pure <see cref="UnitConverter"/> (DM-12). Loads the household's units
/// and the product's conversion overrides, then delegates the math; the loud no-path failure is
/// <see cref="UnitConverter"/>'s own. Lives in Plantry.Web so the Recipes projects never reference Catalog.
/// </summary>
public sealed class RecipesUnitConverterAdapter(IProductRepository products, IUnitRepository units)
    : IUnitConverter
{
    public async Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
    {
        var allUnits = await units.ListAsync(ct);
        var product = await products.FindAsync(ProductId.From(productId), ct);
        IReadOnlyCollection<ProductConversion> conversions = product?.Conversions ?? [];

        return UnitConverter.Convert(amount, fromUnitId, toUnitId, allUnits, conversions);
    }

    /// <summary>
    /// Batched override (plantry-4t0g): pre-loads the household's units and every distinct product's
    /// conversion overrides once, then evaluates every triple in-memory against the same pure
    /// <see cref="UnitConverter.Convert"/> path <see cref="ConvertAsync"/> uses — unlike the per-line
    /// default, this issues at most two queries total regardless of how many triples are checked.
    /// </summary>
    public async Task<IReadOnlySet<(Guid ProductId, Guid FromUnitId, Guid ToUnitId)>> FindUnconvertiblePathsAsync(
        IEnumerable<(Guid ProductId, Guid FromUnitId, Guid ToUnitId)> triples, CancellationToken ct = default)
    {
        var distinctTriples = triples.Distinct().ToList();
        if (distinctTriples.Count == 0)
            return new HashSet<(Guid, Guid, Guid)>();

        var allUnits = await units.ListAsync(ct);
        var productIds = distinctTriples.Select(t => ProductId.From(t.ProductId)).Distinct();
        var loadedProducts = await products.ListWithConversionsAsync(productIds, ct);
        var conversionsByProductId = loadedProducts.ToDictionary(p => p.Id.Value, p => p.Conversions);

        var unconvertible = new HashSet<(Guid ProductId, Guid FromUnitId, Guid ToUnitId)>();
        foreach (var triple in distinctTriples)
        {
            IReadOnlyCollection<ProductConversion> conversions =
                conversionsByProductId.TryGetValue(triple.ProductId, out var c) ? c : [];
            var result = UnitConverter.Convert(1m, triple.FromUnitId, triple.ToUnitId, allUnits, conversions);
            if (result.IsFailure)
                unconvertible.Add(triple);
        }

        return unconvertible;
    }
}
