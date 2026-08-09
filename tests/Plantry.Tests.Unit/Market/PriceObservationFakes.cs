using Plantry.Market.Application;
using Plantry.Market.Domain;

namespace Plantry.Tests.Unit.Market;

/// <summary>
/// Shared in-memory <see cref="IPriceObservationRepository"/> for every Market test tree (Prices and
/// Deals) — both halves write/read the same <c>price_observation</c> table since the Pricing/Deals merge
/// (ADR-024), so one fake with real supersede/window read-filtering behavior serves both, instead of each
/// side keeping its own (a duplicate whose read methods returned null/empty was the hazard removed here —
/// plantry-g3da.1 review). <see cref="ThrowOnAdd"/> models a mid-write failure (throw before a row is
/// recorded) so ConfirmDeal's resumability tests can prove a re-drive links only the missing piece without
/// double-writing.
/// </summary>
internal sealed class FakePriceObservationRepository : IPriceObservationRepository
{
    public List<PriceObservation> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }
    public int? ThrowOnAdd { get; set; }
    /// <summary>Times <see cref="HistoryForProductsAsync"/> was called — lets batching tests prove the
    /// whole-queue read really is one call, not the interface's per-product DIM loop (plantry-gtgl).</summary>
    public int HistoryForProductsCalls { get; private set; }
    /// <summary>Times <see cref="LatestForProductsAsync"/> was called (same batching proof).</summary>
    public int LatestForProductsCalls { get; private set; }
    private int _adds;

    public Task AddAsync(PriceObservation observation, CancellationToken ct = default)
    {
        _adds++;
        if (ThrowOnAdd == _adds)
            throw new InvalidOperationException("simulated deal-observation write failure");

        Items.Add(observation);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PriceObservation>>(Items
            .Where(p => p.Source == PriceSource.Purchase
                && p.StoreId is null
                && !string.IsNullOrWhiteSpace(p.MerchantText)
                && p.SupersededById is null)
            .OrderBy(p => p.ObservedAt)
            .ToList());

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.ProductId == productId
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById is null)
            .MaxBy(p => p.ObservedAt));

    public Task<IReadOnlyList<PriceObservation>> HistoryForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PriceObservation>>(Items
            .Where(p => p.ProductId == productId
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById is null)
            .OrderBy(p => p.ObservedAt)
            .ToList());

    /// <summary>Overrides the interface's per-product DIM, mirroring the production repository's batch
    /// query (same source/supersession filter, oldest-first per product, no-history products absent) —
    /// without this override the batching test would silently exercise the very N+1 loop it claims to
    /// rule out (plantry-gtgl pass-3 critic).</summary>
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PriceObservation>>> HistoryForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        HistoryForProductsCalls++;
        var result = new Dictionary<Guid, IReadOnlyList<PriceObservation>>();
        foreach (var productId in productIds.Distinct())
        {
            var history = Items
                .Where(p => p.ProductId == productId
                    && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                    && p.SupersededById is null)
                .OrderBy(p => p.ObservedAt)
                .ToList();
            if (history.Count > 0)
                result[productId] = history;
        }
        return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<PriceObservation>>>(result);
    }

    /// <summary>Overrides the interface's per-product DIM with the same call-counting rationale as
    /// <see cref="HistoryForProductsAsync"/> — pins the "one latest-purchase read" half of the
    /// whole-queue batching guarantee.</summary>
    public Task<IReadOnlyDictionary<Guid, PriceObservation>> LatestForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        LatestForProductsCalls++;
        var result = new Dictionary<Guid, PriceObservation>();
        foreach (var productId in productIds.Distinct())
        {
            var latest = Items
                .Where(p => p.ProductId == productId
                    && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                    && p.SupersededById is null)
                .MaxBy(p => p.ObservedAt);
            if (latest is not null)
                result[productId] = latest;
        }
        return Task.FromResult<IReadOnlyDictionary<Guid, PriceObservation>>(result);
    }

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.SkuId == skuId
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById is null)
            .MaxBy(p => p.ObservedAt));

    public Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.ProductId == productId && p.Source == PriceSource.Deal
                && p.ValidFrom <= today && p.ValidTo >= today
                && p.SupersededById is null)
            .OrderBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefault());

    public Task<PriceObservation?> ActiveDealForPurchaseAsync(Guid productId, Guid storeId, DateOnly observedDate, decimal purchaseUnitPrice, decimal tolerance, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(p => p.ProductId == productId && p.Source == PriceSource.Deal
                && p.StoreId == storeId
                && p.ValidFrom <= observedDate && p.ValidTo >= observedDate
                && p.SupersededById is null
                // Qualification predicate mirrors the production Postgres query: cheapest QUALIFYING deal.
                && p.UnitPrice.HasValue && p.UnitPrice * (1m + tolerance) >= purchaseUnitPrice)
            // Nulls-last tiebreak (see CheapestActiveDealsForProductsAsync's comment in the production
            // repository): Postgres `ORDER BY unit_price ASC` sorts NULLs last, but LINQ-to-Objects
            // OrderBy on a nullable sorts nulls first — without this, a pack-size-less deal would
            // shadow a cheaper costable one here in a way production never would.
            .OrderBy(p => p.UnitPrice.HasValue ? 0 : 1)
            .ThenBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefault());

    public Task<IReadOnlySet<Guid>> ProductIdsWithAnyObservationAsync(IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var idSet = productIds.ToHashSet();
        var found = Items
            .Where(p => idSet.Contains(p.ProductId) && p.SupersededById is null)
            .Select(p => p.ProductId)
            .ToHashSet();
        return Task.FromResult<IReadOnlySet<Guid>>(found);
    }
}

/// <summary>Fake <see cref="IUnitPriceCalculator"/> — always returns the configured value (soft-fail via null).
/// <see cref="NormalizeCalls"/> lets tests pin whether normalization ran at all — a deal that never carried a
/// unit must skip the calculator entirely, not merely receive a null back (plantry-gtgl).</summary>
internal sealed class FakeUnitPriceCalculator(decimal? returnValue) : IUnitPriceCalculator
{
    public int NormalizeCalls { get; private set; }

    public Task<decimal?> TryNormalizeAsync(decimal price, decimal quantity, Guid unitId, CancellationToken ct = default)
    {
        NormalizeCalls++;
        return Task.FromResult(returnValue);
    }
}
