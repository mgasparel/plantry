using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Unit.Catalog.Application;
using Plantry.Web.Inventory;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="TakeStockCatalogWriterAdapter"/> (plantry-riqy, P4-7/J5/TS-8/TS-9) — the
/// Inventory→Catalog ACL adapter the Take Stock walk uses for its inline Catalog mutations. Each method
/// wraps a Catalog command that is already exhaustively covered in isolation
/// (<c>ProductCommandsTests</c>, <c>CreateVariantCommandTests</c>, <c>CreateGroupedProductCommandTests</c>,
/// <c>SetDefaultLocationCommandTests</c>) — here we pin the adapter's forwarding: the happy path returns
/// the wrapped command's id/void result, and a command failure is re-thrown as
/// <see cref="InvalidOperationException"/>, mirroring <c>CatalogWriterAdapter</c>.
/// </summary>
public sealed class TakeStockCatalogWriterAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();

    private sealed record Fixture(
        FakeProductRepository Products, FakeUnitRepository Units, FakeCategoryRepository Categories,
        FakeLocationRepository Locations, CatalogUnit Unit, Location Location)
    {
        public TakeStockCatalogWriterAdapter Adapter(Guid? household = null) => new(
            Products, Units, Categories, Locations, SystemClock.Instance,
            new FakeTenantContext(household ?? Household.Value));
    }

    private static Fixture MakeFixture()
    {
        var unit = CatalogUnit.Create(Household, "ea", "Each", Dimension.Count, 1m, isBase: true);
        var location = Location.Create(Household, "Pantry", LocationType.Ambient);
        var units = new FakeUnitRepository();
        units.Items.Add(unit);
        var locations = new FakeLocationRepository();
        locations.Items.Add(location);
        return new Fixture(new FakeProductRepository(), units, new FakeCategoryRepository(), locations, unit, location);
    }

    [Fact(DisplayName = "CreateTrackedProductAsync creates a standalone tracked product and returns its id")]
    public async Task CreateTrackedProductAsync_Creates_Tracked_Product()
    {
        var f = MakeFixture();

        var id = await f.Adapter().CreateTrackedProductAsync("Milk", f.Unit.Id.Value, null, f.Location.Id.Value);

        Assert.NotEqual(Guid.Empty, id);
        var product = Assert.Single(f.Products.Items);
        Assert.True(product.TrackStock);
        Assert.Equal(f.Location.Id, product.DefaultLocationId);
    }

    [Fact(DisplayName = "CreateTrackedProductAsync throws InvalidOperationException when the command fails (duplicate name)")]
    public async Task CreateTrackedProductAsync_Throws_On_Command_Failure()
    {
        var f = MakeFixture();
        f.Products.Items.Add(Product.Create(Household, "Milk", f.Unit.Id, SystemClock.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Adapter().CreateTrackedProductAsync("Milk", f.Unit.Id.Value, null, f.Location.Id.Value));

        Assert.Contains("Create tracked product failed", ex.Message);
    }

    [Fact(DisplayName = "CreateTrackedVariantAsync attaches a new variant to the parent group and returns its id")]
    public async Task CreateTrackedVariantAsync_Creates_Variant()
    {
        var f = MakeFixture();
        var parent = Product.Create(Household, "Bubly", f.Unit.Id, SystemClock.Instance, trackStock: false);
        f.Products.Items.Add(parent);

        var id = await f.Adapter().CreateTrackedVariantAsync(parent.Id.Value, "Bubly Lime", null, null, null);

        Assert.NotEqual(Guid.Empty, id);
        var variant = f.Products.Items.Single(p => p.Id.Value == id);
        Assert.Equal(parent.Id, variant.ParentProductId);
    }

    [Fact(DisplayName = "CreateTrackedVariantAsync throws InvalidOperationException when the parent does not exist")]
    public async Task CreateTrackedVariantAsync_Throws_When_Parent_Missing()
    {
        var f = MakeFixture();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Adapter().CreateTrackedVariantAsync(Guid.NewGuid(), "Bubly Lime", null, null, null));

        Assert.Contains("Create tracked variant failed", ex.Message);
    }

    [Fact(DisplayName = "CreateTrackedGroupedProductAsync atomically creates a group and its first variant")]
    public async Task CreateTrackedGroupedProductAsync_Creates_Group_And_Variant()
    {
        var f = MakeFixture();

        var variantId = await f.Adapter().CreateTrackedGroupedProductAsync(
            "Bubly", "Bubly Lime", f.Unit.Id.Value, null, f.Location.Id.Value);

        Assert.NotEqual(Guid.Empty, variantId);
        Assert.Equal(2, f.Products.Items.Count);
        var variant = f.Products.Items.Single(p => p.Id.Value == variantId);
        Assert.True(variant.TrackStock);
        var group = f.Products.Items.Single(p => p.Id != variant.Id);
        Assert.False(group.TrackStock);
        Assert.Equal(group.Id, variant.ParentProductId);
    }

    [Fact(DisplayName = "CreateTrackedGroupedProductAsync throws InvalidOperationException on duplicate group name")]
    public async Task CreateTrackedGroupedProductAsync_Throws_On_Command_Failure()
    {
        var f = MakeFixture();
        f.Products.Items.Add(Product.Create(Household, "Bubly", f.Unit.Id, SystemClock.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Adapter().CreateTrackedGroupedProductAsync(
            "Bubly", "Bubly Lime", f.Unit.Id.Value, null, f.Location.Id.Value));

        Assert.Contains("Create grouped product failed", ex.Message);
    }

    [Fact(DisplayName = "SetDefaultLocationAsync sets the product's default location")]
    public async Task SetDefaultLocationAsync_Sets_Location()
    {
        var f = MakeFixture();
        var product = Product.Create(Household, "Milk", f.Unit.Id, SystemClock.Instance);
        f.Products.Items.Add(product);

        await f.Adapter().SetDefaultLocationAsync(product.Id.Value, f.Location.Id.Value);

        Assert.Equal(f.Location.Id, product.DefaultLocationId);
    }

    [Fact(DisplayName = "SetDefaultLocationAsync throws InvalidOperationException when the product does not exist")]
    public async Task SetDefaultLocationAsync_Throws_When_Product_Missing()
    {
        var f = MakeFixture();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Adapter().SetDefaultLocationAsync(Guid.NewGuid(), f.Location.Id.Value));

        Assert.Contains("Set default location failed", ex.Message);
    }

    [Fact(DisplayName = "AddConversionAsync adds a conversion to the product")]
    public async Task AddConversionAsync_Adds_Conversion()
    {
        var f = MakeFixture();
        var product = Product.Create(Household, "Milk", f.Unit.Id, SystemClock.Instance);
        f.Products.Items.Add(product);
        var otherUnit = CatalogUnit.Create(Household, "L", "Litre", Dimension.Volume, 1m, isBase: true);
        f.Units.Items.Add(otherUnit);

        await f.Adapter().AddConversionAsync(product.Id.Value, f.Unit.Id.Value, otherUnit.Id.Value, 2m);

        var conversion = Assert.Single(product.Conversions);
        Assert.Equal(2m, conversion.Factor);
    }

    [Fact(DisplayName = "AddConversionAsync throws InvalidOperationException when the product does not exist")]
    public async Task AddConversionAsync_Throws_When_Product_Missing()
    {
        var f = MakeFixture();
        var otherUnit = CatalogUnit.Create(Household, "L", "Litre", Dimension.Volume, 1m, isBase: true);
        f.Units.Items.Add(otherUnit);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Adapter().AddConversionAsync(Guid.NewGuid(), f.Unit.Id.Value, otherUnit.Id.Value, 2m));

        Assert.Contains("Add product conversion failed", ex.Message);
    }
}
