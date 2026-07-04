using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Catalog.Domain;

/// <summary>
/// A product-specific unit conversion — cross-dimension or density (e.g. "1 cup flour = 120 g").
/// Universal, product-independent conversions live on <see cref="Unit.FactorToBase"/> instead
/// (catalog.md "dividing line for conversions").
/// </summary>
public sealed class ProductConversion : Entity<ProductConversionId>
{
    /// <summary>Denormalized from the owning <see cref="Product"/> — every RLS-scoped table carries its own household_id.</summary>
    public HouseholdId HouseholdId { get; private set; }
    public ProductId ProductId { get; private set; }
    public UnitId FromUnitId { get; private set; }
    public UnitId ToUnitId { get; private set; }
    public decimal Factor { get; private set; }

    /// <summary>
    /// Provenance of this factor (ADR-022). Defaults to <see cref="ConversionSource.UserConfirmed"/>
    /// so historical rows and existing callers are unchanged; a machine-seeded conversion carries
    /// <see cref="ConversionSource.AiSuggested"/> until the user promotes it.
    /// </summary>
    public ConversionSource Source { get; private set; }

    /// <summary>True while this conversion is an unendorsed machine guess.</summary>
    public bool IsAiSuggested => Source == ConversionSource.AiSuggested;

    private ProductConversion() { } // EF

    private ProductConversion(ProductConversionId id, HouseholdId householdId, ProductId productId, UnitId fromUnitId, UnitId toUnitId, decimal factor, ConversionSource source)
    {
        Id = id;
        HouseholdId = householdId;
        ProductId = productId;
        FromUnitId = fromUnitId;
        ToUnitId = toUnitId;
        Factor = factor;
        Source = source;
    }

    /// <summary>Children are created only through <see cref="Product.AddConversion"/> — keeps the aggregate boundary intact.</summary>
    internal static ProductConversion Create(HouseholdId householdId, ProductId productId, UnitId fromUnitId, UnitId toUnitId, decimal factor, ConversionSource source = ConversionSource.UserConfirmed)
    {
        if (fromUnitId == toUnitId)
            throw new ArgumentException("A conversion's from-unit and to-unit must differ.", nameof(toUnitId));
        if (factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "Conversion factor must be positive.");

        return new ProductConversion(ProductConversionId.New(), householdId, productId, fromUnitId, toUnitId, factor, source);
    }

    /// <summary>
    /// Endorses this conversion — flips <see cref="ConversionSource.AiSuggested"/> to
    /// <see cref="ConversionSource.UserConfirmed"/>. Idempotent: promoting an already-confirmed
    /// conversion is a no-op. Invoked only through <see cref="Product.PromoteConversion"/>.
    /// </summary>
    internal void Promote() => Source = ConversionSource.UserConfirmed;
}
