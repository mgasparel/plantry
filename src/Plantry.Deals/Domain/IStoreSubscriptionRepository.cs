namespace Plantry.Deals.Domain;

/// <summary>
/// Read/write port for the <see cref="StoreSubscription"/> aggregate (§3 / DJ1). The first Deals
/// repository (P5-0 delivered the DbContext, not repos). Mirrors <c>MealPlanRepository</c> — reads and
/// writes on one port — and is RLS-scoped to the current household by <c>DealsDbContext</c>, so every
/// query returns only the signed-in household's rows.
/// </summary>
public interface IStoreSubscriptionRepository
{
    Task<StoreSubscription?> FindAsync(StoreSubscriptionId id, CancellationToken ct = default);

    /// <summary>
    /// The household's subscription for a given store, if any — the reactivation lookup a re-subscribe
    /// uses so a previously paused/unsubscribed store is resumed rather than duplicated
    /// (UNIQUE (household_id, store_id), DD9), preserving its <c>DealMatchMemory</c>.
    /// </summary>
    Task<StoreSubscription?> FindByStoreAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>All of the household's subscriptions (active and inactive) for the §7e management list.</summary>
    Task<List<StoreSubscription>> ListAsync(CancellationToken ct = default);

    Task AddAsync(StoreSubscription subscription, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
