using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Recipes;

public sealed class PriceReaderAdapter(
    PricingQueries pricingQueries,
    IClock clock,
    Plantry.Recipes.Application.ICatalogProductReader catalog,
    IUnitConverter converter) : IPriceReader
{
    public async Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
    {
        var products = await catalog.FindManyWithVariantsAsync([productId], ct);
        if (!products.TryGetValue(productId, out var product)) return null;
        var result = await EffectivePriceRollup.SelectAsync(pricingQueries, CreateContext(product),
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ConvertAsync, ct);
        return result is null ? null : ToPricePoint(result);
    }

    public async Task<IReadOnlyDictionary<Guid, PricePoint>> FindLatestManyAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var ids = productIds.Distinct().ToList();
        var products = await catalog.FindManyWithVariantsAsync(ids, ct);
        var refs = products.Values.SelectMany(p => p.IsParent ? p.VariantProductIds : [p.Id]).Distinct().ToList();
        var observations = await pricingQueries.EffectiveCostablePricesAsync(refs,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct);
        var result = new Dictionary<Guid, PricePoint>();
        foreach (var id in ids)
        {
            if (!products.TryGetValue(id, out var product)) continue;
            var candidate = await EffectivePriceRollup.SelectFromObservationsAsync(CreateContext(product), observations, ConvertAsync, ct);
            if (candidate is not null) result[id] = ToPricePoint(candidate);
        }
        return result;
    }

    private Task<Result<decimal>> ConvertAsync(Guid productId, decimal amount, Guid from, Guid to, CancellationToken ct) =>
        converter.ConvertAsync(productId, amount, from, to, ct);

    private static PriceRollupProduct CreateContext(CatalogProduct product) =>
        new(product.Id, product.DefaultUnitId, product.IsParent,
            product.VariantProductIds.Select(id => new PriceRollupVariant(id,
                product.VariantDefaultUnitIds?.GetValueOrDefault(id) ?? product.DefaultUnitId)).ToList());

    private static PricePoint ToPricePoint(EffectivePriceCandidate candidate) =>
        new(candidate.ConcreteProductId, candidate.Observation.Price, candidate.ConvertedQuantity,
            candidate.RequestedUnitId, candidate.ConvertedUnitPrice);
}
