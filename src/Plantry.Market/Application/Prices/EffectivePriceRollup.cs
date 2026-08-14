using Plantry.Market.Domain;
using Plantry.SharedKernel;

namespace Plantry.Market.Application;

public sealed record PriceRollupProduct(Guid Id, Guid DefaultUnitId, bool IsParent, IReadOnlyList<PriceRollupVariant> Variants);
public sealed record PriceRollupVariant(Guid Id, Guid DefaultUnitId, bool IsArchived = false);

/// <summary>
/// The shared parent-aware effective-price selection (plantry-i07l). One policy feeds every consumer —
/// recipe costing, meal-plan costing, parent price display, and D5 missing-price detection — so they
/// all agree on <em>which</em> candidate wins.
/// </summary>
/// <param name="RequestedProductId">The parent/leaf the caller asked for — the id the result is keyed by.</param>
/// <param name="ConcreteProductId">The concrete (variant) product that actually holds the winning observation — provenance so downstream stock/costing never consumes parent stock or creates a parent observation.</param>
/// <param name="Observation">The raw winning observation, unchanged — costing derives from its own unit.</param>
/// <param name="ConvertedQuantity"><see cref="Observation"/>'s quantity projected into <see cref="RequestedUnitId"/> — a display concern.</param>
/// <param name="ConvertedUnitPrice">Price per 1 <see cref="RequestedUnitId"/> — the comparison basis across candidates; also the display projection.</param>
/// <param name="RequestedUnitId">The reference unit selection compares in: the requested/reference product's default unit (rule 3), or the observation's own unit when that default is unknown (a concrete leaf absent from the catalog resolves as itself, no conversion).</param>
public sealed record EffectivePriceCandidate(
    Guid RequestedProductId, Guid ConcreteProductId, PriceObservation Observation,
    decimal ConvertedQuantity, decimal ConvertedUnitPrice, Guid RequestedUnitId);

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

    /// <summary>
    /// Selects the cheapest usable candidate across the product's price refs (live variants for a
    /// parent; the product itself for a concrete leaf), converting each observation into the shared
    /// reference unit so candidates compare apples-to-apples. A candidate that fails its conversion is
    /// skipped, never shadowed by default. The winning candidate carries the raw observation and the
    /// concrete variant id so costing can convert the observation's own unit onward.
    ///
    /// A concrete product resolves to itself (single-element ref list); per the design decision it does
    /// <b>not</b> require a catalog round-trip just to price a leaf — when the leaf's default unit is
    /// unknown (<see cref="Guid.Empty"/>, i.e. the product is absent from the catalog) the observation is
    /// kept in its own unit (identity), restoring pre-DM-19 behaviour. This must agree with
    /// <c>CostingService</c>, which derives cost from <c>Price / Quantity</c> (not <c>UnitPrice</c>) and
    /// only requires a readable non-zero quantity and a convertible unit.
    /// </summary>
    public static async Task<EffectivePriceCandidate?> SelectFromObservationsAsync(
        PriceRollupProduct product, IReadOnlyDictionary<Guid, PriceObservation> observations,
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> convert,
        CancellationToken ct = default)
    {
        EffectivePriceCandidate? best = null;
        foreach (var reference in Refs(product))
        {
            if (!observations.TryGetValue(reference.Id, out var observation)
                || observation.Quantity <= 0m || observation.UnitId == Guid.Empty)
                continue; // no usable candidate from this ref

            // Reference unit for comparison/projection (rule 3): a parent compares its variants after
            // converting each observation into the parent's (requested/reference) default unit; a
            // concrete product resolves to itself with NO forced conversion — the observation stays in
            // its own unit (identity, no catalog round-trip just to price a leaf). Pre-DM-19 behaviour.
            var referenceUnit = product.IsParent && product.DefaultUnitId != Guid.Empty
                ? product.DefaultUnitId
                : observation.UnitId;

            decimal factor;
            if (referenceUnit == observation.UnitId)
            {
                factor = 1m;
            }
            else
            {
                var converted = await convert(reference.Id, 1m, observation.UnitId, referenceUnit, ct);
                if (converted.IsFailure || converted.Value <= 0m) continue; // unusable — skip this candidate only
                factor = converted.Value;
            }

            var convertedQuantity = observation.Quantity * factor;
            if (convertedQuantity <= 0m) continue;

            var candidate = new EffectivePriceCandidate(product.Id, reference.Id, observation,
                convertedQuantity, observation.Price / convertedQuantity, referenceUnit);
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
