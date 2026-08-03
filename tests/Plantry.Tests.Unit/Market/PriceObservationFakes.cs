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

/// <summary>Fake <see cref="IUnitPriceCalculator"/> — always returns the configured value (soft-fail via null).</summary>
internal sealed class FakeUnitPriceCalculator(decimal? returnValue) : IUnitPriceCalculator
{
    public Task<decimal?> TryNormalizeAsync(decimal price, decimal quantity, Guid unitId, CancellationToken ct = default) =>
        Task.FromResult(returnValue);
}
