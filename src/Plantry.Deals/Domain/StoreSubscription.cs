using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// Aggregate root (§3 / DJ1): the household's standing choice to pull flyers from a
/// <c>catalog.store</c>. One per <c>(household, store)</c> (DD9). The <b>postal code lives here</b>,
/// not on Identity — Flipp's feed is postal-code-scoped, so location is captured per subscription.
/// Unsubscribe is a soft-deactivate; history and match memory are retained (D9).
/// </summary>
public sealed class StoreSubscription : AggregateRoot<StoreSubscriptionId>
{
    private StoreSubscription() { } // EF

    private StoreSubscription(
        StoreSubscriptionId id, HouseholdId householdId, Guid storeId, string postalCode, DateTimeOffset now)
        : base(id)
    {
        HouseholdId = householdId;
        StoreId = storeId;
        PostalCode = postalCode;
        IsActive = true;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Soft-ref → catalog.store; unique per household (DD9).</summary>
    public Guid StoreId { get; private set; }

    /// <summary>The location the flyer is pulled for (Flipp <c>/data?postal_code=…</c>), captured at subscribe.</summary>
    public string PostalCode { get; private set; } = string.Empty;

    /// <summary>Paused/unsubscribed subscriptions are skipped by the worker but retained with their memory.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset? LastPulledAt { get; private set; }

    /// <summary>The last pulled flyer's external id — the dedup anchor (DD5/DL-O5).</summary>
    public string? LastFlyerExternalId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Factory. Starts active. Caller ensures the <c>catalog.store</c> identity exists first (§8).</summary>
    public static StoreSubscription Subscribe(
        HouseholdId householdId, Guid storeId, string postalCode, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            throw new ArgumentException("Postal code must not be blank.", nameof(postalCode));

        return new StoreSubscription(
            StoreSubscriptionId.New(), householdId, storeId, postalCode.Trim(), clock.UtcNow);
    }

    /// <summary>Pauses the subscription without losing history/memory. No-op-safe when already paused.</summary>
    public void Pause(IClock clock)
    {
        if (!IsActive) return;
        IsActive = false;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>Resumes a paused subscription. No-op-safe when already active.</summary>
    public void Resume(IClock clock)
    {
        if (IsActive) return;
        IsActive = true;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Refreshes the postal code the flyer is pulled for (e.g. when a household moves and re-subscribes
    /// to the same merchant). Validates non-blank and trims, mirroring the <see cref="Subscribe"/> guard.
    /// </summary>
    public void UpdatePostalCode(string postalCode, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            throw new ArgumentException("Postal code must not be blank.", nameof(postalCode));

        PostalCode = postalCode.Trim();
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>Soft-deactivates (retains confirmed deals, price history, and match memory — D9).</summary>
    public void Unsubscribe(IClock clock)
    {
        if (!IsActive) return;
        IsActive = false;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>Stamps bookkeeping after a successful pull (the dedup anchor, DD5).</summary>
    public void RecordPull(string flyerExternalId, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(flyerExternalId))
            throw new ArgumentException("Flyer external id must not be blank.", nameof(flyerExternalId));

        LastFlyerExternalId = flyerExternalId;
        LastPulledAt = clock.UtcNow;
        UpdatedAt = clock.UtcNow;
    }
}
