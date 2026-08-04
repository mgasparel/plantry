using Microsoft.Extensions.Logging;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Market.Application;

/// <summary>
/// The §7e read row: a <see cref="StoreSubscription"/> joined to its <c>catalog.store</c> display name
/// and last-pull status. <see cref="LastPulledAt"/> is null until the P5-6 worker records a pull — the
/// UI renders a "not pulled yet" state for those rows.
/// <para>
/// plantry-fsmb: <see cref="LastNewContentAt"/>, <see cref="FlyerValidTo"/>, and
/// <see cref="FlyerValidityLapsed"/> back the §7e badge's three-way state — a no-op re-pull (the
/// statistically normal daily outcome under the P5-6 daily sweep cadence, plantry-rb36) must never make
/// the badge read as fresher than the underlying flyer actually is.
/// </para>
/// </summary>
public sealed record SubscriptionView(
    StoreSubscriptionId Id,
    Guid StoreId,
    string StoreName,
    string PostalCode,
    bool IsActive,
    DateTimeOffset? LastPulledAt,
    string? LastFlyerExternalId,
    DateTimeOffset? LastNewContentAt,
    DateOnly? FlyerValidTo,
    bool FlyerValidityLapsed);

/// <summary>
/// DJ1 application service. Orchestrates the two-context subscribe (ensure <c>catalog.store</c> →
/// create/reactivate <see cref="StoreSubscription"/>), pause/resume/unsubscribe, the directory search,
/// and the §7e read model. The first full-stack Deals↔Catalog slice.
/// </summary>
public sealed class ManageSubscriptions(
    IStoreSubscriptionRepository subscriptions,
    IFlyerImportRepository imports,
    ICatalogStoreReader storeReader,
    ICatalogStoreWriter storeWriter,
    IFlyerSource flyerSource,
    ITenantContext tenant,
    IClock clock,
    ILogger<ManageSubscriptions>? logger = null)
{
    /// <summary>Directory search (stubbed in P5-2). A blank postal code returns no results.</summary>
    public async Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(
        string postalCode, string? nameQuery, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            return [];

        return await flyerSource.SearchDirectoryAsync(postalCode.Trim(), nameQuery?.Trim(), ct);
    }

    /// <summary>
    /// The household's subscriptions (active + inactive) with resolved store names + last-pull status,
    /// oldest first. RLS-scoped by <c>MarketDbContext</c>, so another household's rows never appear.
    /// </summary>
    public async Task<IReadOnlyList<SubscriptionView>> ListAsync(CancellationToken ct = default)
    {
        var subs = await subscriptions.ListAsync(ct);
        if (subs.Count == 0) return [];

        var storeIds = subs.Select(s => s.StoreId).Distinct().ToList();
        var names = await storeReader.ResolveNamesAsync(storeIds, ct);

        // plantry-fsmb: resolve each subscription's most recently pulled flyer's own validity window (one
        // batch read, no N+1 — mirrors ResolveNamesAsync above). ListParsedRefsByStoresAsync returns every
        // distinct (store, window) the household has ever parsed, keyed by flyer_external_id — matched below
        // against each subscription's own LastFlyerExternalId (the DD5 dedup anchor) so the badge reflects
        // THAT flyer's window, not just any parsed import for the store.
        var refs = await imports.ListParsedRefsByStoresAsync(storeIds, ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        return subs
            .Select(s =>
            {
                var flyerRef = s.LastFlyerExternalId is { } externalId
                    ? refs.FirstOrDefault(r => r.StoreId == s.StoreId && r.FlyerExternalId == externalId)
                    : null;
                // Lapsed = ValidTo < today (an upper-bound-only check, critic pass 1) — NOT !window.Contains(today),
                // which would also flag a pre-published not-yet-started window (Flipp ships next week's flyer
                // ahead of time, FlyerSource.cs, with no "must contain today" filter) as falsely "Expired".
                // Mirrors the house convention: ExpiryDisplay's IsExpired = date < today; PriceObservationRepository's
                // still-valid check is ValidTo >= today.
                var lapsed = flyerRef is not null && flyerRef.ValidTo < today;

                return new SubscriptionView(
                    s.Id,
                    s.StoreId,
                    names.TryGetValue(s.StoreId, out var name) ? name : "(unknown store)",
                    s.PostalCode,
                    s.IsActive,
                    s.LastPulledAt,
                    s.LastFlyerExternalId,
                    s.LastNewContentAt,
                    flyerRef?.ValidTo,
                    lapsed);
            })
            .ToList();
    }

    /// <summary>
    /// Subscribe (DJ1). (1) Ensures the <c>catalog.store</c> identity via
    /// <see cref="ICatalogStoreWriter"/> → gets the <c>store_id</c>; (2) creates the
    /// <see cref="StoreSubscription"/> — or <b>reactivates</b> the existing one for that store
    /// (UNIQUE (household_id, store_id), DD9), preserving its <c>DealMatchMemory</c>. Idempotent:
    /// re-subscribing to a paused/unsubscribed store resumes the same row rather than inserting a duplicate.
    /// </summary>
    public async Task<Result<StoreSubscriptionId>> SubscribeAsync(
        string externalRef, string name, string postalCode, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return Error.Unauthorized;

        if (string.IsNullOrWhiteSpace(postalCode))
            return Error.Custom("Deals.BlankPostalCode", "A postal code is required to subscribe to a store.");

        // (1) Ensure the merchant's catalog.store identity (reuse/adopt/reactivate — P5-1).
        var storeId = await storeWriter.EnsureAsync(externalRef, name, ct);

        // (2) Reactivate the existing subscription for this store, or create a new one.
        var existing = await subscriptions.FindByStoreAsync(storeId, ct);
        if (existing is not null)
        {
            existing.UpdatePostalCode(postalCode, clock);
            existing.Resume(clock);
            await subscriptions.SaveChangesAsync(ct);
            logger?.LogInformation(
                "Reactivated subscription {SubscriptionId} for store {StoreId}.", existing.Id.Value, storeId);
            return existing.Id;
        }

        var subscription = StoreSubscription.Subscribe(
            HouseholdId.From(householdGuid), storeId, postalCode.Trim(), clock);
        await subscriptions.AddAsync(subscription, ct);
        await subscriptions.SaveChangesAsync(ct);
        logger?.LogInformation(
            "Created subscription {SubscriptionId} for store {StoreId}.", subscription.Id.Value, storeId);
        return subscription.Id;
    }

    /// <summary>Pauses a subscription (soft-deactivate; skipped by the worker, memory retained — D9).</summary>
    public Task<Result> PauseAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        MutateAsync(id, "Paused", s => s.Pause(clock), ct);

    /// <summary>Resumes a paused subscription.</summary>
    public Task<Result> ResumeAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        MutateAsync(id, "Resumed", s => s.Resume(clock), ct);

    /// <summary>Unsubscribes (soft-deactivate — retains confirmed deals, price history, match memory, D9).</summary>
    public Task<Result> UnsubscribeAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        MutateAsync(id, "Unsubscribed", s => s.Unsubscribe(clock), ct);

    private async Task<Result> MutateAsync(
        StoreSubscriptionId id, string operation, Action<StoreSubscription> mutate, CancellationToken ct)
    {
        var subscription = await subscriptions.FindAsync(id, ct);
        if (subscription is null)
        {
            logger?.LogWarning("{Operation} failed — subscription {SubscriptionId} not found.", operation, id.Value);
            return Error.NotFound;
        }

        mutate(subscription);
        await subscriptions.SaveChangesAsync(ct);
        logger?.LogInformation("{Operation} subscription {SubscriptionId}.", operation, id.Value);
        return Result.Success();
    }
}
