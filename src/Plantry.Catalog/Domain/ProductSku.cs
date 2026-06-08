using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Catalog.Domain;

/// <summary>A purchasable pack size of a <see cref="Product"/> — e.g. "2 L carton", "500 g bag".</summary>
public sealed class ProductSku : Entity<ProductSkuId>
{
    /// <summary>Denormalized from the owning <see cref="Product"/> — every RLS-scoped table carries its own household_id.</summary>
    public HouseholdId HouseholdId { get; private set; }
    public ProductId ProductId { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public decimal? SizeQuantity { get; private set; }
    public UnitId? SizeUnitId { get; private set; }

    private ProductSku() { } // EF

    private ProductSku(ProductSkuId id, HouseholdId householdId, ProductId productId, string label, decimal? sizeQuantity, UnitId? sizeUnitId)
    {
        Id = id;
        HouseholdId = householdId;
        ProductId = productId;
        Label = label;
        SizeQuantity = sizeQuantity;
        SizeUnitId = sizeUnitId;
    }

    /// <summary>Children are created only through <see cref="Product.AddSku"/> — keeps the aggregate boundary intact.</summary>
    internal static ProductSku Create(HouseholdId householdId, ProductId productId, string label, decimal? sizeQuantity, UnitId? sizeUnitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (sizeQuantity is <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeQuantity), "Size quantity must be positive when provided.");

        return new ProductSku(ProductSkuId.New(), householdId, productId, label.Trim(), sizeQuantity, sizeUnitId);
    }
}
