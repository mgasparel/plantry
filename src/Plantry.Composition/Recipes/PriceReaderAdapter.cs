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
        products.TryGetValue(productId, out var product);
        var result = await EffectivePriceRollup.SelectAsync(pricingQueries, CreateContext(productId, product),
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ConvertAsync, ct);
        return result is null ? null : ToPricePoint(result);
    }

    public async Task<IReadOnlyDictionary<Guid, PricePoint>> FindLatestManyAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var ids = productIds.Distinct().ToList();
        var products = await catalog.FindManyWithVariantsAsync(ids, ct);
        // One batched read over every price ref (leaf id, or each live variant of a parent) — no
        // per-product/per-variant N+1 (plantry-hbol, DM-19). Ids absent from the catalog are leaves
        // (self) and are included as themselves.
        var refs = new HashSet<Guid>();
        foreach (var id in ids)
            foreach (var r in RefsFor(products, id))
                refs.Add(r);

        var observations = await pricingQueries.EffectiveCostablePricesAsync(refs,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct);

        var result = new Dictionary<Guid, PricePoint>();
        foreach (var id in ids)
        {
            products.TryGetValue(id, out var product);
            var candidate = await EffectivePriceRollup.SelectFromObservationsAsync(CreateContext(id, product), observations, ConvertAsync, ct);
            if (candidate is not null) result[id] = ToPricePoint(candidate);
        }
        return result;
    }

    private static IReadOnlyList<Guid> RefsFor(IReadOnlyDictionary<Guid, CatalogProduct> products, Guid id) =>
        products.TryGetValue(id, out var product) && product.IsParent
            ? product.VariantProductIds
            : [id];

    private Task<Result<decimal>> ConvertAsync(Guid productId, decimal amount, Guid from, Guid to, CancellationToken ct) =>
        converter.ConvertAsync(productId, amount, from, to, ct);

    /// <summary>
    /// Builds the rollup context for a requested id. A product absent from the catalog is treated as a
    /// concrete leaf (self) with an unknown default unit — pre-DM-19 behaviour the recipe/meal-plan
    /// deal-aware costing tests rely on (a leaf needs no catalog round-trip to price). A parent keeps
    /// each live variant as its own rollup variant, populated with the variant's default unit.
    /// </summary>
    private static PriceRollupProduct CreateContext(Guid productId, CatalogProduct? product)
    {
        if (product is null)
            return new PriceRollupProduct(productId, Guid.Empty, IsParent: false, []);

        return new PriceRollupProduct(product.Id, product.DefaultUnitId, product.IsParent,
            product.VariantProductIds.Select(id => new PriceRollupVariant(id,
                product.VariantDefaultUnitIds?.GetValueOrDefault(id) ?? product.DefaultUnitId)).ToList());
    }

    /// <summary>
    /// Emits the returned <see cref="PricePoint"/> in the winning observation's own unit
    /// (<c>UnitId</c>/<c>Quantity</c>/<c>Price</c> as observed) with the concrete variant's id for
    /// provenance. <c>CostingService</c> then converts that observation unit to the line unit via its
    /// own <c>ResolveConverterAsync</c> — the single requested-unit path (plantry-i07l design decision,
    /// restoring pre-DM-19 concrete behaviour). The rollup's projected
    /// <see cref="EffectivePriceCandidate.ConvertedQuantity"/>/<see cref="EffectivePriceCandidate.ConvertedUnitPrice"/>
    /// are a display concern only.
    /// </summary>
    private static PricePoint ToPricePoint(EffectivePriceCandidate candidate) =>
        new(candidate.ConcreteProductId, candidate.Observation.Price, candidate.Observation.Quantity,
            candidate.Observation.UnitId, candidate.Observation.UnitPrice);
}
