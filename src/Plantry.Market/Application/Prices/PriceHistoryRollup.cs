using Plantry.Market.Domain;
using Plantry.SharedKernel;

namespace Plantry.Market.Application;

/// <summary>
/// A product's price history, rolled up across live variants when the product is a parent (plantry-oh27.5).
/// Mirrors <see cref="EffectivePriceRollup"/>'s parent-aware selection, but for the full trend series
/// rather than a single winning candidate — the parent product detail page sparkline/median and the
/// deals review page's "you pay $X" purchase context when a deal is matched to a parent.
/// </summary>
/// <param name="RequestedProductId">The parent/leaf the caller asked for — the id the result is keyed by.</param>
/// <param name="ReferenceUnitId">The unit <see cref="Points"/>' <see cref="PriceHistoryPoint.UnitPrice"/> is
/// per: the parent's default unit for a parent (every point was converted into it), or
/// <see cref="Guid.Empty"/> for a concrete leaf — a leaf's points are the calculator's own base-unit
/// normalization (see <see cref="PriceHistoryPoint"/>'s doc comment), a unit this projection has no id
/// for and must not mislabel as the leaf's <c>DefaultUnitId</c> (a different scale whenever the default
/// unit is not itself the dimension's base unit).</param>
/// <param name="Points">Oldest-first, unit price per 1 <see cref="ReferenceUnitId"/>; ties broken by
/// contributing product id for determinism.</param>
/// <param name="ContributingProductIds">Leaves whose history contributed at least one point.</param>
/// <param name="SkippedProductIds">Leaves that had history but whose conversion into the reference unit
/// failed for at least one observation.</param>
public sealed record RolledUpPriceHistory(
    Guid RequestedProductId,
    Guid ReferenceUnitId,
    IReadOnlyList<PriceHistoryPoint> Points,
    IReadOnlyList<Guid> ContributingProductIds,
    IReadOnlyList<Guid> SkippedProductIds);

public static class PriceHistoryRollup
{
    public static async Task<RolledUpPriceHistory> ForProductAsync(
        PricingQueries pricing, PriceRollupProduct product,
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> convert,
        CancellationToken ct = default)
    {
        var results = await ForProductsAsync(pricing, [product], convert, ct);
        return results[product.Id];
    }

    public static async Task<IReadOnlyDictionary<Guid, RolledUpPriceHistory>> ForProductsAsync(
        PricingQueries pricing, IReadOnlyList<PriceRollupProduct> products,
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> convert,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, RolledUpPriceHistory>();

        // Leaf rule 1: identical to PriceHistoryAsync(id) today — same points, no conversion, no catalog
        // round-trip — just batched across every requested leaf in one call.
        var leaves = products.Where(p => !p.IsParent).ToList();
        if (leaves.Count > 0)
        {
            var leafHistories = await pricing.PriceHistoryForProductsAsync(leaves.Select(l => l.Id), ct);
            foreach (var leaf in leaves)
            {
                var points = leafHistories.TryGetValue(leaf.Id, out var p) ? p : [];
                result[leaf.Id] = new RolledUpPriceHistory(leaf.Id, Guid.Empty, points,
                    points.Count > 0 ? [leaf.Id] : [], []);
            }
        }

        // Parent rule 2: one batched raw-observation read over every live variant across every requested
        // parent, then each observation is converted into ITS parent's default unit exactly the way
        // EffectivePriceRollup.SelectFromObservationsAsync derives ConvertedUnitPrice (shared helper —
        // the two rollups can never disagree).
        var parents = products.Where(p => p.IsParent).ToList();
        if (parents.Count > 0)
        {
            var variantIds = parents.SelectMany(EffectivePriceRollup.Refs).Select(v => v.Id).Distinct().ToList();
            var rawHistories = variantIds.Count == 0
                ? new Dictionary<Guid, IReadOnlyList<PriceObservation>>()
                : await pricing.RawHistoryForProductsAsync(variantIds, ct);

            // Memoizes (variantId, fromUnit, toUnit) -> factor within this call: a variant with many
            // observations recorded in the same unit would otherwise re-issue an identical convert()
            // round trip per observation (N+1) instead of once per distinct triple.
            var conversionCache = new Dictionary<(Guid VariantId, Guid From, Guid To), Task<Result<decimal>>>();
            Task<Result<decimal>> CachedConvert(Guid variantId, decimal amount, Guid from, Guid to, CancellationToken token)
            {
                var key = (variantId, from, to);
                if (conversionCache.TryGetValue(key, out var cached)) return cached;
                var task = convert(variantId, amount, from, to, token);
                conversionCache[key] = task;
                return task;
            }

            foreach (var parent in parents)
            {
                var referenceUnitId = parent.DefaultUnitId;
                var points = new List<(DateTimeOffset ObservedAt, Guid ProductId, PriceHistoryPoint Point)>();
                var contributing = new HashSet<Guid>();
                var skipped = new HashSet<Guid>();

                foreach (var reference in EffectivePriceRollup.Refs(parent))
                {
                    if (!rawHistories.TryGetValue(reference.Id, out var observations) || observations.Count == 0)
                        continue;

                    foreach (var observation in observations)
                    {
                        var converted = await EffectivePriceRollup.ConvertToReferenceUnitAsync(
                            reference.Id, observation, referenceUnitId, CachedConvert, ct);
                        if (converted.IsFailure)
                        {
                            skipped.Add(reference.Id);
                            continue;
                        }

                        points.Add((observation.ObservedAt, reference.Id, new PriceHistoryPoint(
                            DateOnly.FromDateTime(observation.ObservedAt.UtcDateTime), converted.Value.UnitPrice)));
                        contributing.Add(reference.Id);
                    }
                }

                var orderedPoints = points
                    .OrderBy(p => p.ObservedAt)
                    .ThenBy(p => p.ProductId) // rule 4: ties by product id for determinism
                    .Select(p => p.Point)
                    .ToList();

                result[parent.Id] = new RolledUpPriceHistory(parent.Id, referenceUnitId, orderedPoints,
                    contributing.OrderBy(id => id).ToList(), skipped.OrderBy(id => id).ToList());
            }
        }

        return result;
    }
}
