using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Catalog.Domain;

/// <summary>A grocery category — provides the expiry-default fallback chain (DM-11) and store-layout grouping.</summary>
public sealed class Category : AggregateRoot<CategoryId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int? DefaultDueDays { get; private set; }
    public int SortOrder { get; private set; }

    /// <summary>
    /// Hue in degrees on the oklch colour wheel (0–359). Used to derive the category colour chip's
    /// background and foreground via CSS: <c>oklch(0.92 0.045 {Hue})</c> for bg,
    /// <c>oklch(0.42 0.105 {Hue})</c> for fg. Null means no hue assigned (renders neutral chip).
    /// </summary>
    public int? Hue { get; private set; }

    /// <summary>
    /// Reference data is soft-deleted (catalog.md / Gate 6): because <c>products.category_id</c>
    /// is a bare cross-row id with no FK, hard-deleting would silently orphan referencing
    /// products. Archived categories stay resolvable for those references but drop out of the
    /// management list and the product-edit dropdowns.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; private set; }
    public bool IsArchived => ArchivedAt is not null;

    private Category() { } // EF

    private Category(CategoryId id, HouseholdId householdId, string name, int? defaultDueDays, int sortOrder, int? hue)
    {
        Id = id;
        HouseholdId = householdId;
        Name = name;
        DefaultDueDays = defaultDueDays;
        SortOrder = sortOrder;
        Hue = hue;
    }

    public static Category Create(HouseholdId householdId, string name, int? defaultDueDays = null, int sortOrder = 0, int? hue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (defaultDueDays is < 0)
            throw new ArgumentOutOfRangeException(nameof(defaultDueDays));
        if (hue is < 0 or > 359)
            throw new ArgumentOutOfRangeException(nameof(hue));

        return new Category(CategoryId.New(), householdId, name.Trim(), defaultDueDays, sortOrder, hue);
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void SetDefaultDueDays(int? days)
    {
        if (days is < 0) throw new ArgumentOutOfRangeException(nameof(days));
        DefaultDueDays = days;
    }

    public void SetSortOrder(int sortOrder) => SortOrder = sortOrder;

    public void SetHue(int? hue)
    {
        if (hue is < 0 or > 359) throw new ArgumentOutOfRangeException(nameof(hue));
        Hue = hue;
    }

    public void Archive(IClock clock)
    {
        if (IsArchived) return;
        ArchivedAt = clock.UtcNow;
    }

    public void Unarchive()
    {
        ArchivedAt = null;
    }
}
