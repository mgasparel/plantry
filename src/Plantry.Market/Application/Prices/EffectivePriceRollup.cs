using Plantry.Market.Domain;
using Plantry.SharedKernel;

namespace Plantry.Market.Application;

public sealed record PriceRollupProduct(Guid Id, Guid DefaultUnitId, bool IsParent, IReadOnlyList<PriceRollupVariant> Variants);
public sealed record PriceRollupVariant(Guid Id, Guid DefaultUnitId, bool IsArchived = false);
public sealed record EffectivePriceCandidate(Guid RequestedProductId, Guid ConcreteProductId, PriceObservation Observation, decimal ConvertedQuantity, decimal ConvertedUnitPrice, Guid RequestedUnitId);

public static class EffectivePriceRollup
{
    public static async Task<EffectivePriceCandidate?> SelectAsync(
        PricingQueries pricing, PriceRollupProduct product, DateOnly today,
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> convert,
        CancellationToken ct = default)
    {
        var ids = Refs(product).Select(v => v.Id).ToList();
        var observations = await pricing.EffectiveCostablePricesAsync(ids, today, ct);
        return await SelectFromObservationsAsync(product, observations, convert, ct);
    }

    public static async Task<EffectivePriceCandidate?> SelectFromObservationsAsync(
        PriceRollupProduct product, IReadOnlyDictionary<Guid, PriceObservation> observations,
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> convert,
        CancellationToken ct = default)
    {
        EffectivePriceCandidate? best = null;
        foreach (var reference in Refs(product))
        {
            if (!observations.TryGetValue(reference.Id, out var observation) || observation.Quantity <= 0 || observation.UnitId == Guid.Empty || !observation.UnitPrice.HasValue || observation.UnitPrice.Value <= 0)
                continue;
            var factor = await convert(reference.Id, 1m, observation.UnitId, reference.DefaultUnitId, ct);
            if (factor.IsFailure || factor.Value <= 0) continue;
            var convertedQuantity = observation.Quantity * factor.Value;
            var candidate = new EffectivePriceCandidate(product.Id, reference.Id, observation,
                convertedQuantity, observation.Price / convertedQuantity, product.DefaultUnitId);
            if (best is null || candidate.ConvertedUnitPrice < best.ConvertedUnitPrice)
                best = candidate;
        }
        return best;
    }

    private static IReadOnlyList<PriceRollupVariant> Refs(PriceRollupProduct product) =>
        product.IsParent
            ? product.Variants.Where(v => !v.IsArchived).ToList()
            : [new PriceRollupVariant(product.Id, product.DefaultUnitId)];
}
