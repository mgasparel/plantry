using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// Aggregate root (§6 / D4 / SPEC §6b): the remembered <c>(store, normalized_name) → product</c>
/// resolution that skips re-review on the next pull. One per <c>(household, store, normalized_name)</c>
/// (DD3). <b>Store-scoped by design</b> — the same normalized name can resolve to different products
/// across stores for brand-less generics, and a household-global key would risk a silent cross-store
/// mis-auto-confirm. A null <see cref="ProductId"/> is a <b>negative memory</b> ("not a tracked product").
/// </summary>
public sealed class DealMatchMemory : AggregateRoot<DealMatchMemoryId>
{
    private DealMatchMemory() { } // EF

    private DealMatchMemory(
        DealMatchMemoryId id, HouseholdId householdId, Guid storeId, NormalizedName normalizedName,
        string rawName, Guid? productId, Guid? by, DateTimeOffset now)
        : base(id)
    {
        HouseholdId = householdId;
        StoreId = storeId;
        NormalizedName = normalizedName.Value;
        NormalizerVersion = normalizedName.NormalizerVersion;
        RawName = rawName;
        ProductId = productId;
        LastConfirmedByUserId = by;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Soft-ref → catalog.store — store-scoped by design (DD3).</summary>
    public Guid StoreId { get; private set; }

    /// <summary>With <see cref="StoreId"/>, unique per household (DD3) — the auto-confirm key.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    /// <summary>The raw advertised name this key was derived from — retained for normalizer-change backfill (DD4).</summary>
    public string RawName { get; private set; } = string.Empty;

    /// <summary>The <see cref="DealNormalizer"/> version that produced <see cref="NormalizedName"/>; a bump flags a backfill (DD4).</summary>
    public int NormalizerVersion { get; private set; }

    /// <summary>The remembered product. <b>Null = negative memory</b> ("not a tracked product", DJ4 step 4).</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>Soft-ref → identity user; provenance.</summary>
    public Guid? LastConfirmedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Factory (positive memory): remembers a <c>(store, normalized_name) → product</c> mapping.</summary>
    public static DealMatchMemory Remember(
        HouseholdId householdId, Guid storeId, NormalizedName normalizedName,
        string rawName, Guid productId, Guid? by, IClock clock) =>
        new(DealMatchMemoryId.New(), householdId, storeId, normalizedName, rawName, productId, by, clock.UtcNow);

    /// <summary>Factory (negative memory): remembers "not a tracked product" for this key (DL-O3).</summary>
    public static DealMatchMemory RememberNegative(
        HouseholdId householdId, Guid storeId, NormalizedName normalizedName,
        string rawName, Guid? by, IClock clock) =>
        new(DealMatchMemoryId.New(), householdId, storeId, normalizedName, rawName, productId: null, by, clock.UtcNow);

    /// <summary>Rewrites the mapping to a different product (a correction, DD11).</summary>
    public void Repoint(Guid productId, Guid? by, IClock clock)
    {
        ProductId = productId;
        LastConfirmedByUserId = by;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>Turns this into a negative memory ("not a tracked product").</summary>
    public void Forget(IClock clock)
    {
        ProductId = null;
        UpdatedAt = clock.UtcNow;
    }
}
