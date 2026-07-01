using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Catalog.Application;

/// <summary>
/// L1 unit tests for <see cref="CreateGroupedProductCommand"/> (plantry-40n6).
/// Verifies: creates group + first variant atomically; inherits attributes from the group;
/// enforces unique names for both group and variant; enforces cross-ref validation;
/// rejects unknown unit/category/location; rejects when no tenant.
/// </summary>
public sealed class CreateGroupedProductCommandTests
{
    private static readonly HouseholdId HouseholdId = Plantry.SharedKernel.HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private record Fixture(
        CatalogUnit Unit,
        Category Category,
        Location Location,
        FakeProductRepository Products,
        FakeUnitRepository Units,
        FakeCategoryRepository Categories,
        FakeLocationRepository Locations)
    {
        public CreateGroupedProductCommand BuildCommand(
            string groupName = "Milk",
            string variantName = "Oat Milk",
            Guid? unitId = null,
            Guid? categoryId = null,
            Guid? locationId = null,
            FakeTenantContext? tenant = null) =>
            new(
                groupName,
                variantName,
                unitId ?? Unit.Id.Value,
                categoryId,
                locationId,
                Products,
                Units,
                Categories,
                Locations,
                Clock,
                tenant ?? new FakeTenantContext(HouseholdId.Value));
    }

    private static Fixture MakeFixture()
    {
        var unit = CatalogUnit.Create(HouseholdId, "L", "Litre", Dimension.Volume, 0.001m, isBase: false);
        var category = Category.Create(HouseholdId, "Dairy");
        var location = Location.Create(HouseholdId, "Fridge", LocationType.Ambient);

        var products = new FakeProductRepository();
        var units = new FakeUnitRepository();
        units.Items.Add(unit);
        var categories = new FakeCategoryRepository();
        categories.Items.Add(category);
        var locations = new FakeLocationRepository();
        locations.Items.Add(location);

        return new Fixture(unit, category, location, products, units, categories, locations);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_Group_And_Variant_In_One_SaveChanges()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        // Repo holds exactly two products (group + variant) and SaveChanges was called once.
        Assert.Equal(2, f.Products.Items.Count);
        Assert.Equal(1, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Returns_VariantId_Not_GroupId()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.True(variant.TrackStock);          // the variant can hold stock
        Assert.NotNull(variant.ParentProductId);  // it has a parent
    }

    [Fact]
    public async Task Group_Is_Abstract_Parent_That_Cannot_Hold_Stock()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        var group = f.Products.Items.Single(p => p.Id != result.Value);
        Assert.False(group.TrackStock);   // group cannot hold stock
        Assert.True(group.IsParent);      // denormalized flag set
        Assert.True(group.HasVariants);
        Assert.Null(group.ParentProductId);
    }

    [Fact]
    public async Task Variant_Is_Linked_To_Group_As_Parent()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        var group = f.Products.Items.Single(p => p.Id != result.Value);
        Assert.Equal(group.Id, variant.ParentProductId);
    }

    [Fact]
    public async Task Variant_Inherits_Unit_Category_Location_From_Group()
    {
        var f = MakeFixture();
        var cmd = f.BuildCommand(categoryId: f.Category.Id.Value, locationId: f.Location.Id.Value);

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        var group = f.Products.Items.Single(p => p.Id != result.Value);

        // Both group and variant get the same unit, category, location from the command.
        Assert.Equal(f.Unit.Id, group.DefaultUnitId);
        Assert.Equal(f.Category.Id, group.CategoryId);
        Assert.Equal(f.Location.Id, group.DefaultLocationId);

        Assert.Equal(f.Unit.Id, variant.DefaultUnitId);
        Assert.Equal(f.Category.Id, variant.CategoryId);
        Assert.Equal(f.Location.Id, variant.DefaultLocationId);
    }

    [Fact]
    public async Task Group_Name_Is_Trimmed()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand(groupName: "  Milk  ", variantName: "Oat Milk").ExecuteAsync();

        Assert.True(result.IsSuccess);
        var group = f.Products.Items.Single(p => p.Id != result.Value);
        Assert.Equal("Milk", group.Name);
    }

    [Fact]
    public async Task Variant_Name_Is_Trimmed()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand(groupName: "Milk", variantName: "  Oat Milk  ").ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal("Oat Milk", variant.Name);
    }

    // ── Error paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Unauthorized_When_No_Tenant()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand(tenant: new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_When_Group_Name_Already_Exists()
    {
        var f = MakeFixture();
        // Pre-populate a product with the same name as the intended group.
        var existing = Product.Create(HouseholdId, "Milk", f.Unit.Id, Clock);
        f.Products.Items.Add(existing);

        var result = await f.BuildCommand(groupName: "Milk").ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateProductName", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
        Assert.Single(f.Products.Items); // nothing added
    }

    [Fact]
    public async Task Rejects_When_Variant_Name_Already_Exists()
    {
        var f = MakeFixture();
        var existing = Product.Create(HouseholdId, "Oat Milk", f.Unit.Id, Clock);
        f.Products.Items.Add(existing);

        var result = await f.BuildCommand(variantName: "Oat Milk").ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateProductName", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
        Assert.Single(f.Products.Items); // nothing added
    }

    [Fact]
    public async Task Rejects_Unknown_Unit()
    {
        var f = MakeFixture();
        var unknownUnitId = Guid.NewGuid();

        var result = await f.BuildCommand(unitId: unknownUnitId).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownUnit", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Unknown_Category()
    {
        var f = MakeFixture();
        var unknownCategoryId = Guid.NewGuid();

        var result = await f.BuildCommand(categoryId: unknownCategoryId).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownCategory", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Unknown_Location()
    {
        var f = MakeFixture();
        var unknownLocationId = Guid.NewGuid();

        var result = await f.BuildCommand(locationId: unknownLocationId).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownLocation", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task No_Products_Written_When_Group_Name_Collides()
    {
        // Even though the variant name is free, we must not write anything if the group name collides.
        var f = MakeFixture();
        var existingGroup = Product.Create(HouseholdId, "Milk", f.Unit.Id, Clock);
        f.Products.Items.Add(existingGroup);

        var result = await f.BuildCommand(groupName: "Milk", variantName: "Brand New Oat Milk").ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Single(f.Products.Items); // only the pre-existing product
    }
}
