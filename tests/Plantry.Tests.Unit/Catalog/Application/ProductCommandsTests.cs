using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Catalog.Application;

/// <summary>
/// L1 unit tests for the <c>trackStock</c> parameter added to <see cref="UpdateProductCommand"/>
/// (plantry-9ndg). Covers: flips a standalone product's <see cref="Product.TrackStock"/> in both
/// directions, and confirms a parent product's flag is left untouched no matter what was posted —
/// a parent is an abstract grouping that can never hold stock, so the command ignores the
/// parameter entirely for it (the single source of truth backing the UI's hidden toggle).
/// </summary>
public sealed class ProductCommandsTests
{
    private static readonly HouseholdId HouseholdId = Plantry.SharedKernel.HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private record Fixture(CatalogUnit Unit, FakeProductRepository Products, FakeUnitRepository Units, FakeCategoryRepository Categories, FakeLocationRepository Locations)
    {
        public UpdateProductCommand BuildCommand(Product product, bool trackStock, string? name = null) =>
            new(
                product.Id,
                name ?? product.Name,
                Unit.Id.Value,
                product.CategoryId?.Value,
                product.DefaultLocationId?.Value,
                product.DefaultDueDays,
                product.DefaultDueDaysAfterOpening,
                product.DefaultDueDaysAfterFreezing,
                product.DefaultDueDaysAfterThawing,
                trackStock,
                Products,
                Units,
                Categories,
                Locations,
                Clock);
    }

    private static Fixture MakeFixture()
    {
        var unit = CatalogUnit.Create(HouseholdId, "ea", "Each", Dimension.Count, 1m, isBase: true);
        var units = new FakeUnitRepository();
        units.Items.Add(unit);
        return new Fixture(unit, new FakeProductRepository(), units, new FakeCategoryRepository(), new FakeLocationRepository());
    }

    [Fact]
    public async Task Flips_Standalone_Product_From_Tracked_To_Untracked()
    {
        var f = MakeFixture();
        var product = Product.Create(HouseholdId, "Whole Milk", f.Unit.Id, Clock, trackStock: true);
        f.Products.Items.Add(product);

        var result = await f.BuildCommand(product, trackStock: false).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(product.TrackStock);
    }

    [Fact]
    public async Task Flips_Standalone_Product_From_Untracked_To_Tracked()
    {
        var f = MakeFixture();
        var product = Product.Create(HouseholdId, "Table Salt", f.Unit.Id, Clock, trackStock: false);
        f.Products.Items.Add(product);

        var result = await f.BuildCommand(product, trackStock: true).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(product.TrackStock);
    }

    [Fact]
    public async Task Parent_Product_TrackStock_Flag_Is_Unaffected_By_Posted_Value()
    {
        var f = MakeFixture();
        var parent = Product.Create(HouseholdId, "Bubly", f.Unit.Id, Clock, trackStock: true);
        parent.SetHasVariants(true, Clock);
        f.Products.Items.Add(parent);
        Assert.True(parent.IsParent);

        var result = await f.BuildCommand(parent, trackStock: false).ExecuteAsync();

        Assert.True(result.IsSuccess);
        // The posted "false" must be ignored entirely — a parent can never hold stock
        // (CanHoldStock is false), so the flag stays at whatever it already was.
        Assert.True(parent.TrackStock);
    }
}
