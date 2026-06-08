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
}
