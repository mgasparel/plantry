using Plantry.SharedKernel;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using Dimension = Plantry.Catalog.Domain.Dimension;

namespace Plantry.Tests.Unit.Catalog.Domain;

public sealed class UnitTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();

    [Fact]
    public void Create_Sets_Properties_And_Trims_Strings()
    {
        var unit = CatalogUnit.Create(HouseholdId, "  g  ", "  Gram  ", Dimension.Mass, 1m, isBase: true);

        Assert.Equal("g", unit.Code);
        Assert.Equal("Gram", unit.Name);
        Assert.Equal(Dimension.Mass, unit.Dimension);
        Assert.Equal(1m, unit.FactorToBase);
        Assert.True(unit.IsBase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_Rejects_NonPositive_FactorToBase(decimal factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CatalogUnit.Create(HouseholdId, "g", "Gram", Dimension.Mass, factor));
    }

    [Fact]
    public void Create_Rejects_Base_Unit_With_Factor_Other_Than_One()
    {
        Assert.Throws<ArgumentException>(() =>
            CatalogUnit.Create(HouseholdId, "kg", "Kilogram", Dimension.Mass, 1000m, isBase: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Rejects_Blank_Code(string code)
    {
        Assert.Throws<ArgumentException>(() =>
            CatalogUnit.Create(HouseholdId, code, "Gram", Dimension.Mass, 1m));
    }

    [Fact]
    public void Rename_Trims_And_Updates_Name()
    {
        var unit = CatalogUnit.Create(HouseholdId, "g", "Gram", Dimension.Mass, 1m);

        unit.Rename("  Grams  ");

        Assert.Equal("Grams", unit.Name);
    }

    [Fact]
    public void Rename_Rejects_Blank_Name()
    {
        var unit = CatalogUnit.Create(HouseholdId, "g", "Gram", Dimension.Mass, 1m);

        Assert.Throws<ArgumentException>(() => unit.Rename("  "));
    }
}
