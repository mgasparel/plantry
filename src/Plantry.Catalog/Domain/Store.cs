using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Catalog.Domain;

/// <summary>
/// The merchant identity (catalog.md DM-16 <c>store</c>) — household-scoped reference data of the
/// same shape as <see cref="Location"/> / <see cref="Unit"/> / <see cref="Category"/>. Catalog-owned,
/// soft-deleted (DM-4), and referenced by id from Pricing (<c>price_observation.store_id</c>) and
/// Deals (<c>store_subscription</c> / <c>flyer_import</c> / <c>deal</c>).
/// </summary>
public sealed class Store : AggregateRoot<StoreId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// The flyer-source (Flipp) store identifier — how a <c>deals.store_subscription</c> / ingest
    /// pull resolves this merchant in the external directory. Null for a manually-named store; a
    /// partial <c>UNIQUE (household_id, external_ref) WHERE external_ref IS NOT NULL</c> keeps the
    /// non-null values unambiguous so <see cref="Application.EnsureStoreCommand"/> can resolve by it.
    /// </summary>
    public string? ExternalRef { get; private set; }

    /// <summary>
    /// Reference data is soft-deleted (catalog.md / DM-4): the store id is a bare cross-context
    /// soft-ref with no FK, so hard-deleting would silently orphan referencing price observations
    /// and deals. Archived stores stay resolvable but drop out of the management list; re-subscribing
    /// to an archived merchant reactivates its row (deals.md §store_subscription) rather than minting a
    /// duplicate, so its <c>deal_match_memory</c> still applies.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; private set; }
    public bool IsArchived => ArchivedAt is not null;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Store() { } // EF

    private Store(StoreId id, HouseholdId householdId, string name, string? externalRef, DateTimeOffset now)
    {
        Id = id;
        HouseholdId = householdId;
        Name = name;
        ExternalRef = externalRef;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Creates a store. A manually-named merchant passes <paramref name="externalRef"/> = null; an
    /// ensure-by-external-identity (subscribe) passes the resolved directory id.
    /// </summary>
    public static Store Create(HouseholdId householdId, string name, IClock clock, string? externalRef = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedRef = string.IsNullOrWhiteSpace(externalRef) ? null : externalRef.Trim();
        return new Store(StoreId.New(), householdId, name.Trim(), normalizedRef, clock.UtcNow);
    }

    public void Rename(string name, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Touch(clock);
    }

    /// <summary>
    /// Back-fills the external directory id onto an existing (manually-named) merchant row so a
    /// subscribe adopts it rather than colliding with <c>UNIQUE (household_id, name)</c>
    /// (deals.md §store_subscription; the "user created 'FreshCo' before subscribing" case).
    /// </summary>
    public void AdoptExternalRef(string externalRef, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRef);
        ExternalRef = externalRef.Trim();
        Touch(clock);
    }

    public void Archive(IClock clock)
    {
        if (IsArchived) return;
        ArchivedAt = clock.UtcNow;
        Touch(clock);
    }

    public void Unarchive(IClock clock)
    {
        if (!IsArchived) return;
        ArchivedAt = null;
        Touch(clock);
    }

    private void Touch(IClock clock) => UpdatedAt = clock.UtcNow;
}
