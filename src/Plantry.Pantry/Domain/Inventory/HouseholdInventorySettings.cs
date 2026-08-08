using Plantry.SharedKernel;

namespace Plantry.Pantry.Domain;

/// <summary>
/// Aggregate root holding a household's Inventory-level settings. Currently a single value:
/// the "expiring soon" horizon (in days) that defines when a lot renders as
/// <c>ExpiryTone.Soon</c> and appears in the Today expiring-soon widget — and, read through an
/// ACL port, drives the Recipes browse "use soon" filter so every "expiring soon" surface agrees
/// by construction (plantry-5yhd).
///
/// One row per household; seeded lazily on first write. Until a household edits the setting there
/// is no row and callers fall back to <see cref="DefaultExpiringSoonDays"/> (7), which preserves
/// the pre-existing Inventory behaviour. HouseholdId is both the PK and the aggregate identity,
/// mirroring the single-per-household <c>HouseholdPlanningSettings</c> pattern.
/// </summary>
public sealed class HouseholdInventorySettings
{
    /// <summary>Fallback horizon (days) when a household has not configured one — preserves prior behaviour.</summary>
    public const int DefaultExpiringSoonDays = 7;

    /// <summary>Inclusive lower bound for a configured horizon (at least one day of look-ahead).</summary>
    public const int MinExpiringSoonDays = 1;

    /// <summary>Inclusive upper bound for a configured horizon (a year of look-ahead is the practical ceiling).</summary>
    public const int MaxExpiringSoonDays = 365;

    // Required by EF
    private HouseholdInventorySettings() { }

    private HouseholdInventorySettings(HouseholdId householdId)
    {
        HouseholdId = householdId;
    }

    /// <summary>Primary key — one settings record per household.</summary>
    public HouseholdId HouseholdId { get; private set; }

    /// <summary>
    /// Number of days from today within which a dated lot counts as "expiring soon". Defaults to
    /// <see cref="DefaultExpiringSoonDays"/> on a freshly seeded record.
    /// </summary>
    public int ExpiringSoonDays { get; private set; } = DefaultExpiringSoonDays;

    /// <summary>
    /// The household's default storage location for cases with no more specific default (plantry-iypo)
    /// — currently consulted only by <c>InventoryProducerAdapter.ProduceAsync</c> as the middle rung
    /// between a yielded product's own <c>Product.DefaultLocationId</c> and the alphabetically-first
    /// active location: product default (if set and active) wins, then this household default (if set
    /// and active), then the alphabetical fallback. Lives alongside <see cref="ExpiringSoonDays"/> on
    /// this same per-household settings row (rather than a cross-context Identity field) because both
    /// halves of the check — "does this household have a default?" and "is that location still
    /// active?" — resolve inside this one context (<c>Location</c> lives in the same
    /// <c>Plantry.Pantry</c> assembly, ADR-024) with no ACL indirection needed. Null means unset.
    /// </summary>
    public LocationId? DefaultLocationId { get; private set; }

    /// <summary>Creates a new settings record for the household with the default horizon (lazy seeding).</summary>
    public static HouseholdInventorySettings Create(HouseholdId householdId) =>
        new(householdId);

    /// <summary>
    /// Sets the "expiring soon" horizon. Guarded to the
    /// [<see cref="MinExpiringSoonDays"/>, <see cref="MaxExpiringSoonDays"/>] range so the value is
    /// always a meaningful look-ahead window.
    /// </summary>
    public void SetExpiringSoonDays(int days)
    {
        if (days < MinExpiringSoonDays || days > MaxExpiringSoonDays)
            throw new ArgumentOutOfRangeException(
                nameof(days), days,
                $"Expiring-soon horizon must be between {MinExpiringSoonDays} and {MaxExpiringSoonDays} days.");
        ExpiringSoonDays = days;
    }

    /// <summary>
    /// Sets (or clears, passing null) the household's default storage location (plantry-iypo). No
    /// existence/active-location validation here — the write path
    /// (<c>HouseholdDefaultLocationService.SetDefaultLocationAsync</c>) owns that, since it has
    /// <see cref="ILocationRepository"/> in hand and this aggregate does not.
    /// </summary>
    public void SetDefaultLocationId(LocationId? locationId) => DefaultLocationId = locationId;
}
