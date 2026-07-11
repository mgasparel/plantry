using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Catalog.Domain;

/// <summary>
/// A measurement unit within a dimension (mass / volume / count). Within-dimension conversion
/// is linear scaling via <see cref="FactorToBase"/> — there is no pairwise conversion table
/// (catalog.md "Two simplifications adopted").
/// </summary>
public sealed class Unit : AggregateRoot<UnitId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public Dimension Dimension { get; private set; }
    public decimal FactorToBase { get; private set; }
    public bool IsBase { get; private set; }

    /// <summary>
    /// How quantities in this unit render at the presentation edge (quantity-display.md Q2).
    /// Display-only — never affects stored quantities, scaling, or consumption. Defaults to
    /// <see cref="DisplayStyle.Decimal"/>; households opt volume units like cup/tbsp/tsp into
    /// <see cref="DisplayStyle.Fraction"/> on the Catalog → Units page.
    /// </summary>
    public DisplayStyle DisplayStyle { get; private set; } = DisplayStyle.Decimal;

    private Unit() { } // EF

    private Unit(UnitId id, HouseholdId householdId, string code, string name,
        Dimension dimension, decimal factorToBase, bool isBase)
    {
        Id = id;
        HouseholdId = householdId;
        Code = code;
        Name = name;
        Dimension = dimension;
        FactorToBase = factorToBase;
        IsBase = isBase;
        DisplayStyle = DisplayStyle.Decimal;
    }

    public static Unit Create(HouseholdId householdId, string code, string name,
        Dimension dimension, decimal factorToBase, bool isBase = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (factorToBase <= 0)
            throw new ArgumentOutOfRangeException(nameof(factorToBase), "factor_to_base must be positive.");
        if (isBase && factorToBase != 1m)
            throw new ArgumentException("The base unit of a dimension must have factor_to_base = 1.", nameof(factorToBase));

        return new Unit(UnitId.New(), householdId, code.Trim(), name.Trim(), dimension, factorToBase, isBase);
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>
    /// Sets how quantities in this unit render (quantity-display.md Q2). Display-only: changes no
    /// stored quantity or downstream calculation.
    /// </summary>
    public void SetDisplayStyle(DisplayStyle style) => DisplayStyle = style;
}
