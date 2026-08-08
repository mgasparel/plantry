using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Pantry.Domain;

/// <summary>A storage location. Frozen locations trigger freeze/thaw expiry recalculation (SPEC §7b).</summary>
public sealed class Location : AggregateRoot<LocationId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public LocationType Type { get; private set; }

    /// <summary>
    /// Reference data is soft-deleted (catalog.md / Gate 6): <c>products.default_location_id</c>
    /// is a bare cross-row id with no FK, so hard-deleting would silently orphan referencing
    /// products. Archived locations stay resolvable but drop out of the management list and the
    /// product-edit dropdowns.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; private set; }
    public bool IsArchived => ArchivedAt is not null;

    /// <summary>
    /// When a Take Stock walk of this location last completed (plantry-hp67). Location-level only
    /// for v1 — no per-product verified timestamp is recorded. Null means the location has never
    /// had a completed walk.
    /// </summary>
    public DateTimeOffset? LastCountedAt { get; private set; }

    private Location() { } // EF

    private Location(LocationId id, HouseholdId householdId, string name, LocationType type)
    {
        Id = id;
        HouseholdId = householdId;
        Name = name;
        Type = type;
    }

    public static Location Create(HouseholdId householdId, string name, LocationType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Location(LocationId.New(), householdId, name.Trim(), type);
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public bool IsFrozen => Type == LocationType.Frozen;

    public void Archive(IClock clock)
    {
        if (IsArchived) return;
        ArchivedAt = clock.UtcNow;
    }

    public void Unarchive()
    {
        ArchivedAt = null;
    }

    /// <summary>
    /// Stamps this location as counted (a Take Stock walk completed here). Always overwrites with
    /// the current time — unlike <see cref="Archive"/> there is no "already done" guard, since each
    /// completed walk is a fresh observation that should supersede the last one.
    /// </summary>
    public void MarkCounted(IClock clock)
    {
        LastCountedAt = clock.UtcNow;
    }
}
