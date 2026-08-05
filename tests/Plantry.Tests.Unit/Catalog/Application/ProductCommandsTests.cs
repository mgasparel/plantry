using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

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
        public UpdateProductCommand BuildCommand(
            Product product,
            bool trackStock,
            string? name = null,
            int? defaultDueDaysAfterFreezing = null,
            int? defaultDueDaysAfterThawing = null,
            bool? neverExpiresAfterFreezing = null,
            bool? neverExpiresAfterThawing = null,
            bool isProduced = false) =>
            new(
                product.Id,
                name ?? product.Name,
                Unit.Id.Value,
                product.CategoryId?.Value,
                product.DefaultLocationId?.Value,
                product.DefaultDueDays,
                product.DefaultDueDaysAfterOpening,
                defaultDueDaysAfterFreezing ?? product.DefaultDueDaysAfterFreezing,
                defaultDueDaysAfterThawing ?? product.DefaultDueDaysAfterThawing,
                trackStock,
                isProduced,
                Products,
                Units,
                Categories,
                Locations,
                Clock,
                neverExpiresAfterFreezing: neverExpiresAfterFreezing,
                neverExpiresAfterThawing: neverExpiresAfterThawing);
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

    [Fact]
    public async Task Update_Sets_IsProduced_From_Editor_Override()
    {
        // plantry-sn6v: a user clears the auto-minted flag on a yield product they've started buying.
        var f = MakeFixture();
        var product = Product.Create(HouseholdId, "Nacho Cheese", f.Unit.Id, Clock, isProduced: true);
        f.Products.Items.Add(product);
        Assert.True(product.IsProduced);

        var result = await f.BuildCommand(product, trackStock: true, isProduced: false).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(product.IsProduced);
    }

    [Fact]
    public async Task Parent_Product_IsProduced_Flag_Is_Unaffected_By_Posted_Value()
    {
        // plantry-sn6v: like TrackStock (Parent_Product_TrackStock_Flag_Is_Unaffected_By_Posted_Value
        // above), IsProduced is meaningless for a parent — parents can never hold stock, so they
        // never enter the restock-suggestion candidacy loop, and InheritFrom deliberately does not
        // cascade the flag to variants. The posted value is ignored for a parent.
        var f = MakeFixture();
        var parent = Product.Create(HouseholdId, "Bubly", f.Unit.Id, Clock, trackStock: true);
        parent.SetHasVariants(true, Clock);
        f.Products.Items.Add(parent);

        var result = await f.BuildCommand(parent, trackStock: false, isProduced: true).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(parent.IsProduced);
    }

    [Fact]
    public async Task Update_Transitions_Never_Product_To_Set_Days()
    {
        var f = MakeFixture();
        var product = Product.Create(HouseholdId, "Frozen Peas", f.Unit.Id, Clock);
        product.SetNeverExpiryOverrides(true, true, Clock);
        f.Products.Items.Add(product);

        var result = await f.BuildCommand(
            product,
            trackStock: true,
            defaultDueDaysAfterFreezing: 90,
            defaultDueDaysAfterThawing: 14,
            neverExpiresAfterFreezing: false,
            neverExpiresAfterThawing: false).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(product.NeverExpiresAfterFreezing);
        Assert.False(product.NeverExpiresAfterThawing);
        Assert.Equal(90, product.DefaultDueDaysAfterFreezing);
        Assert.Equal(14, product.DefaultDueDaysAfterThawing);
    }
}
