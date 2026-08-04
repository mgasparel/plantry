using Plantry.Pantry.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.Tests.Unit.Catalog.Application;
using Plantry.Web.Recipes;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="RecipesQuantityFormatterAdapter"/> (plantry-riqy, quantity-display.md §7) —
/// the Recipes→Catalog ACL adapter that loads the household's units once and delegates formatting to
/// the pure, already-well-covered <see cref="QuantityFormatting.Format"/> (see
/// <c>InclusionPreviewQuantityDisplayTests</c> for the formatting rules themselves). Here we pin the
/// adapter's own contribution: unit loading + the empty-request short-circuit, plus the unknown-unit
/// fallback so the seam between "unit known to this household" and "unit unknown" is proven at the
/// adapter boundary too.
/// </summary>
public sealed class RecipesQuantityFormatterAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();

    [Fact(DisplayName = "FormatAsync returns an empty map for an empty request list")]
    public async Task ShortCircuits_On_Empty_Requests()
    {
        var result = await new RecipesQuantityFormatterAdapter(new FakeUnitRepository()).FormatAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FormatAsync formats a known-unit request using the household's loaded units")]
    public async Task Formats_Known_Unit_Request()
    {
        // A Fraction-style unit: the known-unit path renders 0.5 as "½", while the unknown-unit
        // fallback would render the raw decimal "0.5" — so this assertion can only pass if the
        // adapter actually loaded the household's units and handed them to the formatter.
        var unit = CatalogUnit.Create(Household, "cup", "Cup", Dimension.Volume, 240m);
        unit.SetDisplayStyle(DisplayStyle.Fraction);
        var units = new FakeUnitRepository();
        units.Items.Add(unit);
        var request = new QuantityFormatRequest("line-1", 0.5m, unit.Id.Value, Simplify: false);

        var result = await new RecipesQuantityFormatterAdapter(units).FormatAsync([request]);

        var formatted = result["line-1"];
        Assert.Equal(unit.Id.Value, formatted.UnitId);
        Assert.Equal("½", formatted.Amount);
    }

    [Fact(DisplayName = "FormatAsync falls back to the historical decimal for a unit unknown to the household")]
    public async Task Falls_Back_For_Unknown_Unit()
    {
        var unitId = Guid.NewGuid();
        var request = new QuantityFormatRequest("line-1", 2.5m, unitId, Simplify: false);

        var result = await new RecipesQuantityFormatterAdapter(new FakeUnitRepository()).FormatAsync([request]);

        var formatted = result["line-1"];
        Assert.Equal(unitId, formatted.UnitId);
        Assert.Equal("2.5", formatted.Amount);
    }
}
