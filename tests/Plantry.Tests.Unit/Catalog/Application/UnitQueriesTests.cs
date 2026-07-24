using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Catalog.Application;

/// <summary>
/// L1 coverage for <see cref="UnitQueries.OrderForDropdown"/> (plantry-n9iw) — the shared ordering
/// rule every unit &lt;select&gt; dropdown groups by: Dimension in the enum's own declaration order
/// (Mass -&gt; Volume -&gt; Count), then Code within each dimension.
/// </summary>
public sealed class UnitQueriesTests
{
    private static readonly HouseholdId Household = HouseholdId.From(Guid.NewGuid());

    private static CatalogUnit Make(string code, string name, Dimension dimension) =>
        CatalogUnit.Create(Household, code, name, dimension, factorToBase: 1m, isBase: false);

    [Fact]
    public void OrderForDropdown_Groups_Mass_Then_Volume_Then_Count()
    {
        var each = Make("ea", "Each", Dimension.Count);
        var litre = Make("L", "Litre", Dimension.Volume);
        var gram = Make("g", "Gram", Dimension.Mass);

        // Deliberately fed out of dimension order to prove the method re-sorts, not merely preserves input.
        var result = UnitQueries.OrderForDropdown([each, litre, gram]);

        Assert.Equal([gram, litre, each], result);
    }

    [Fact]
    public void OrderForDropdown_Orders_Within_Group_By_Code()
    {
        var kilogram = Make("kg", "Kilogram", Dimension.Mass);
        var gram = Make("g", "Gram", Dimension.Mass);
        var milligram = Make("mg", "Milligram", Dimension.Mass);

        var result = UnitQueries.OrderForDropdown([kilogram, gram, milligram]);

        Assert.Equal([gram, kilogram, milligram], result);
    }

    [Fact]
    public void OrderForDropdown_Code_Sort_Is_Case_Insensitive()
    {
        var upper = Make("KG", "Kilogram", Dimension.Mass);
        var lower = Make("g", "Gram", Dimension.Mass);

        var result = UnitQueries.OrderForDropdown([upper, lower]);

        Assert.Equal([lower, upper], result);
    }

    [Fact]
    public void OrderForDropdown_Empty_Input_Returns_Empty()
    {
        var result = UnitQueries.OrderForDropdown([]);

        Assert.Empty(result);
    }
}
