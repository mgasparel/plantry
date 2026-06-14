using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Catalog.Domain;

/// <summary>
/// The rich Catalog aggregate root (catalog.md "product"). Owns its <see cref="ProductSku"/>
/// and <see cref="ProductConversion"/> children, the parent/variant relationship (max depth 1),
/// and the four expiry-default fields that seed DM-11's fallback chain.
/// </summary>
public sealed class Product : AggregateRoot<ProductId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ProductId? ParentProductId { get; private set; }
    public CategoryId? CategoryId { get; private set; }
    public UnitId DefaultUnitId { get; private set; }
    public LocationId? DefaultLocationId { get; private set; }

    /// <summary>
    /// Whether this product participates in quantity accounting (catalog.md "untracked staples").
    /// True for ordinary goods; <c>false</c> for an untracked staple (salt, water, oil) that is always
    /// on hand — Inventory skips its lots, Recipes fulfillment treats it as always satisfied, Cook
    /// skips its consume, and Shopping never auto-adds it. Inline auto-create (from a recipe author or
    /// intake) mints the product with <c>track_stock = false</c>; the user can enable tracking later.
    /// </summary>
    public bool TrackStock { get; private set; } = true;
    public int? DefaultDueDays { get; private set; }
    public int? DefaultDueDaysAfterOpening { get; private set; }
    public int? DefaultDueDaysAfterFreezing { get; private set; }
    public int? DefaultDueDaysAfterThawing { get; private set; }

    /// <summary>
    /// Denormalized: true once another product has been made a variant of this one. Lets
    /// <see cref="CanHoldStock"/> answer locally — whether a product "is a parent" is otherwise
    /// a cross-aggregate fact. Maintained by the application layer (it already loads both
    /// products to enforce the depth-1 invariant in <c>MakeVariantCommand</c>).
    /// </summary>
    public bool HasVariants { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private readonly List<ProductSku> _skus = [];
    private readonly List<ProductConversion> _conversions = [];
    public IReadOnlyList<ProductSku> Skus => _skus.AsReadOnly();
    public IReadOnlyList<ProductConversion> Conversions => _conversions.AsReadOnly();

    private Product() { } // EF

    private Product(ProductId id, HouseholdId householdId, string name, UnitId defaultUnitId, bool trackStock, DateTimeOffset now)
    {
        Id = id;
        HouseholdId = householdId;
        Name = name;
        DefaultUnitId = defaultUnitId;
        TrackStock = trackStock;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <param name="trackStock">
    /// Pass <c>false</c> to mint an untracked staple (inline auto-create, C12); defaults to <c>true</c>
    /// for ordinary stock-holding goods.
    /// </param>
    public static Product Create(HouseholdId householdId, string name, UnitId defaultUnitId, IClock clock, bool trackStock = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Product(ProductId.New(), householdId, name.Trim(), defaultUnitId, trackStock, clock.UtcNow);
    }

    public bool IsArchived => ArchivedAt is not null;
    public bool IsVariant => ParentProductId is not null;
    public bool IsParent => HasVariants;

    /// <summary>Parent products are abstract groupings — only concrete (non-parent) products may hold stock.</summary>
    public bool CanHoldStock => !HasVariants;

    public void Rename(string name, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Touch(clock);
    }

    public void SetCategory(CategoryId? categoryId, IClock clock)
    {
        CategoryId = categoryId;
        Touch(clock);
    }

    public void SetDefaultUnit(UnitId unitId, IClock clock)
    {
        DefaultUnitId = unitId;
        Touch(clock);
    }

    public void SetDefaultLocation(LocationId? locationId, IClock clock)
    {
        DefaultLocationId = locationId;
        Touch(clock);
    }

    /// <summary>Enable or disable quantity accounting for this product (e.g. promoting an untracked staple to a tracked good).</summary>
    public void SetTrackStock(bool trackStock, IClock clock)
    {
        TrackStock = trackStock;
        Touch(clock);
    }

    public void SetExpiryDefaults(
        int? defaultDueDays,
        int? defaultDueDaysAfterOpening,
        int? defaultDueDaysAfterFreezing,
        int? defaultDueDaysAfterThawing,
        IClock clock)
    {
        if (defaultDueDays is < 0 || defaultDueDaysAfterOpening is < 0
            || defaultDueDaysAfterFreezing is < 0 || defaultDueDaysAfterThawing is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultDueDays), "Expiry defaults must not be negative.");
        }

        DefaultDueDays = defaultDueDays;
        DefaultDueDaysAfterOpening = defaultDueDaysAfterOpening;
        DefaultDueDaysAfterFreezing = defaultDueDaysAfterFreezing;
        DefaultDueDaysAfterThawing = defaultDueDaysAfterThawing;
        Touch(clock);
    }

    /// <summary>
    /// One-time inheritance handshake for a parent/variant group (notes: a parent's expiry
    /// defaults and conversions "should be inherited... a user should still be able to
    /// override"). Copies each expiry-default field from <paramref name="parent"/> only where
    /// this product's own is unset, and copies the parent's conversions only when this product
    /// has none of its own. Called both when a variant is first attached and whenever the
    /// parent's inheritable values change — but values that have already flowed across do not
    /// re-sync on later parent edits (manual fix-up only, by design).
    /// </summary>
    public void InheritFrom(Product parent, IClock clock)
    {
        var changed = false;

        if (DefaultDueDays is null && parent.DefaultDueDays is { } dueDays) { DefaultDueDays = dueDays; changed = true; }
        if (DefaultDueDaysAfterOpening is null && parent.DefaultDueDaysAfterOpening is { } afterOpening) { DefaultDueDaysAfterOpening = afterOpening; changed = true; }
        if (DefaultDueDaysAfterFreezing is null && parent.DefaultDueDaysAfterFreezing is { } afterFreezing) { DefaultDueDaysAfterFreezing = afterFreezing; changed = true; }
        if (DefaultDueDaysAfterThawing is null && parent.DefaultDueDaysAfterThawing is { } afterThawing) { DefaultDueDaysAfterThawing = afterThawing; changed = true; }

        if (_conversions.Count == 0 && parent.Conversions.Count > 0)
        {
            foreach (var conversion in parent.Conversions)
                _conversions.Add(ProductConversion.Create(HouseholdId, Id, conversion.FromUnitId, conversion.ToUnitId, conversion.Factor));
            changed = true;
        }

        if (changed) Touch(clock);
    }

    public ProductSku AddSku(string label, decimal? sizeQuantity, UnitId? sizeUnitId, IClock clock)
    {
        var sku = ProductSku.Create(HouseholdId, Id, label, sizeQuantity, sizeUnitId);
        _skus.Add(sku);
        Touch(clock);
        return sku;
    }

    public void RemoveSku(ProductSkuId skuId, IClock clock)
    {
        var sku = _skus.SingleOrDefault(s => s.Id == skuId)
            ?? throw new InvalidOperationException($"SKU '{skuId}' does not belong to product '{Id}'.");
        _skus.Remove(sku);
        Touch(clock);
    }

    public ProductConversion AddConversion(UnitId fromUnitId, UnitId toUnitId, decimal factor, IClock clock)
    {
        var conversion = ProductConversion.Create(HouseholdId, Id, fromUnitId, toUnitId, factor);
        _conversions.Add(conversion);
        Touch(clock);
        return conversion;
    }

    public void RemoveConversion(ProductConversionId conversionId, IClock clock)
    {
        var conversion = _conversions.SingleOrDefault(c => c.Id == conversionId)
            ?? throw new InvalidOperationException($"Conversion '{conversionId}' does not belong to product '{Id}'.");
        _conversions.Remove(conversion);
        Touch(clock);
    }

    /// <summary>
    /// Attaches this product to <paramref name="parentId"/> as a variant. Self-parenting and
    /// turning an existing parent into a variant (depth &gt; 1) are rejected here; whether
    /// <paramref name="parentId"/> is itself already a variant is a cross-aggregate check the
    /// application layer must make before calling this (catalog.md "max depth 1").
    /// </summary>
    public void MakeVariantOf(ProductId parentId, IClock clock)
    {
        if (parentId == Id)
            throw new ArgumentException("A product cannot be its own parent.", nameof(parentId));
        if (HasVariants)
            throw new InvalidOperationException("A parent product cannot itself become a variant (max depth 1).");

        ParentProductId = parentId;
        Touch(clock);
    }

    public void DetachFromParent(IClock clock)
    {
        ParentProductId = null;
        Touch(clock);
    }

    /// <summary>Maintained by the application layer when it attaches/detaches the first/last variant.</summary>
    public void SetHasVariants(bool hasVariants, IClock clock)
    {
        HasVariants = hasVariants;
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
