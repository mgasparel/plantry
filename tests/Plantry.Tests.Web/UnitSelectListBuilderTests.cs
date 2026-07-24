using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.Web.Pages.Shared;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web;

/// <summary>
/// L1 coverage for <see cref="UnitSelectListBuilder"/> (plantry-n9iw) — the one shared construction
/// point every unit &lt;select&gt; uses to build a dimension-grouped optgroup SelectListItem list,
/// whether the caller holds a live <see cref="Unit"/> (<see cref="UnitSelectListBuilder.BuildFromUnits"/>)
/// or an anti-corruption-layer DTO (<see cref="UnitSelectListBuilder.Build{T}"/>).
/// </summary>
public sealed class UnitSelectListBuilderTests
{
    private static readonly HouseholdId Household = HouseholdId.From(Guid.NewGuid());

    private static CatalogUnit MakeUnit(string code, string name, Dimension dimension) =>
        CatalogUnit.Create(Household, code, name, dimension, factorToBase: 1m, isBase: false);

    private sealed record FakeUnitDto(Guid Id, string Code, string DimensionRaw);

    [Fact]
    public void BuildFromUnits_Groups_Mass_Then_Volume_Then_Count_And_Sets_Text_Value()
    {
        var each = MakeUnit("ea", "Each", Dimension.Count);
        var litre = MakeUnit("L", "Litre", Dimension.Volume);
        var gram = MakeUnit("g", "Gram", Dimension.Mass);

        var result = UnitSelectListBuilder.BuildFromUnits(
            [each, litre, gram],
            u => u.Id.Value.ToString(),
            u => $"{u.Code} — {u.Name}");

        Assert.Equal(3, result.Count);
        Assert.Equal("g — Gram", result[0].Text);
        Assert.Equal(gram.Id.Value.ToString(), result[0].Value);
        Assert.Equal("Mass", result[0].Group!.Name);

        Assert.Equal("L — Litre", result[1].Text);
        Assert.Equal("Volume", result[1].Group!.Name);

        Assert.Equal("ea — Each", result[2].Text);
        Assert.Equal("Count", result[2].Group!.Name);
    }

    [Fact]
    public void Build_Generic_Orders_By_Dimension_Then_SortKey_For_AclDtos()
    {
        var units = new[]
        {
            new FakeUnitDto(Guid.NewGuid(), "ea", "count"),
            new FakeUnitDto(Guid.NewGuid(), "kg", "mass"),
            new FakeUnitDto(Guid.NewGuid(), "g", "mass"),
            new FakeUnitDto(Guid.NewGuid(), "L", "volume"),
        };

        var result = UnitSelectListBuilder.Build(
            units,
            u => u.Id.ToString(),
            u => u.Code,
            u => DimensionExtensions.Parse(u.DimensionRaw),
            u => u.Code);

        Assert.Equal(["g", "kg", "L", "ea"], result.Select(r => r.Text));
        Assert.Equal(["Mass", "Mass", "Volume", "Count"], result.Select(r => r.Group!.Name));
    }

    [Fact]
    public void BuildFromUnits_Empty_Input_Returns_Empty()
    {
        var result = UnitSelectListBuilder.BuildFromUnits([], u => u.Id.Value.ToString(), u => u.Code);

        Assert.Empty(result);
    }
}
