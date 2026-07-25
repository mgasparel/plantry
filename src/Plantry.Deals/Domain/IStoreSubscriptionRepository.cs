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

    /// <summary>
    /// The household's <c>is_active</c> subscriptions — the P5-6 worker's per-household work list (DJ2).
    /// RLS-scoped, so it only ever returns the armed household's subscriptions.
    /// </summary>
    Task<List<StoreSubscription>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>Cross-tenant.</b> The most recent <see cref="StoreSubscription.LastPulledAt"/> across every
    /// household's subscriptions, or <c>null</c> if none has ever recorded a successful pull — the P5-6
    /// worker's boot due-check (plantry-rb36): whether a sweep is already due, or how long until it is.
    /// Mirrors <see cref="Plantry.Identity.Domain.IHouseholdRepository.ListAllIdsAsync"/>'s carve-out
    /// (<c>deals.store_subscription</c>'s RLS policy grants the same pre-auth exception). Callers MUST
    /// invoke this with no <c>TenantContext</c> armed; run inside an armed household it collapses to that
    /// one household's own max.
    /// <para>
    /// <b>Caveat:</b> <see cref="StoreSubscription.LastPulledAt"/> only advances on a pull that returned a
    /// flyer (<see cref="StoreSubscription.RecordPull"/>), so this approximates "last successful pull," not
    /// "last sweep attempt." Acceptable for the due-check — a pull that consistently finds nothing new is
    /// indistinguishable from one that never ran, and re-sweeping in that case is the safe default.
    /// </para>
    /// </summary>
    Task<DateTimeOffset?> GetLastPulledAtAcrossHouseholdsAsync(CancellationToken ct = default);

    Task AddAsync(StoreSubscription subscription, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
