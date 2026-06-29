using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Catalog.Application;

/// <summary>
/// L1 unit tests for <see cref="CreateVariantCommand"/> (plantry-8r7o).
/// Verifies: creates and links a variant in one step; inherits parent attributes by default;
/// honours overrides; enforces depth-1 invariant; rejects missing parent; rejects duplicate names;
/// rejects unknown cross-references; rejects when no tenant.
/// </summary>
public sealed class CreateVariantCommandTests
{
    private static readonly HouseholdId HouseholdId = Plantry.SharedKernel.HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    /// <summary>
    /// Builds a fully-seeded test fixture: a parent product, plus one unit/category/location
    /// each wired up to the parent's attribute Ids, so the cross-ref validation in
    /// <see cref="CreateVariantCommand"/> can find them.
    /// </summary>
    private record Fixture(
        Product Parent,
        CatalogUnit Unit,
        Category Category,
        Location Location,
        FakeProductRepository Products,
        FakeUnitRepository Units,
        FakeCategoryRepository Categories,
        FakeLocationRepository Locations)
    {
        public CreateVariantCommand BuildCommand(
            string name = "Whole Milk",
            Guid? unitOverride = null,
            Guid? categoryOverride = null,
            Guid? locationOverride = null,
            FakeTenantContext? tenant = null) =>
            new(
                Parent.Id,
                name,
                unitOverride,
                categoryOverride,
                locationOverride,
                Products,
                Units,
                Categories,
                Locations,
                Clock,
                tenant ?? new FakeTenantContext(HouseholdId.Value));
    }

    private static Fixture MakeFixture(string parentName = "Milk", bool includeParentInRepo = true)
    {
        // Create unit, category, location, parent — the unit is a standalone "each" unit.
        var unit = CatalogUnit.Create(HouseholdId, "ea", "Each", Dimension.Count, 1m, isBase: true);
        var category = Category.Create(HouseholdId, "Dairy");
        var location = Location.Create(HouseholdId, "Fridge", LocationType.Ambient);

        var parent = Product.Create(HouseholdId, parentName, unit.Id, Clock);
        parent.SetCategory(category.Id, Clock);
        parent.SetDefaultLocation(location.Id, Clock);
        parent.SetExpiryDefaults(7, 3, 90, 2, Clock);

        var products = new FakeProductRepository();
        if (includeParentInRepo) products.Items.Add(parent);

        var units = new FakeUnitRepository();
        units.Items.Add(unit);

        var categories = new FakeCategoryRepository();
        categories.Items.Add(category);

        var locations = new FakeLocationRepository();
        locations.Items.Add(location);

        return new Fixture(parent, unit, category, location, products, units, categories, locations);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_Variant_And_Links_To_Parent_In_One_Step()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        // Repo now holds the parent + the new variant.
        Assert.Equal(2, f.Products.Items.Count);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal(f.Parent.Id, variant.ParentProductId);
        Assert.True(f.Parent.IsParent);
        Assert.Equal(1, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Variant_Inherits_Unit_Category_Location_From_Parent_When_No_Override()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal(f.Parent.DefaultUnitId, variant.DefaultUnitId);
        Assert.Equal(f.Parent.CategoryId, variant.CategoryId);
        Assert.Equal(f.Parent.DefaultLocationId, variant.DefaultLocationId);
    }

    [Fact]
    public async Task Variant_Inherits_Expiry_Defaults_From_Parent()
    {
        var f = MakeFixture(); // parent has 7/3/90/2 expiry defaults

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal(f.Parent.DefaultDueDays, variant.DefaultDueDays);
        Assert.Equal(f.Parent.DefaultDueDaysAfterOpening, variant.DefaultDueDaysAfterOpening);
        Assert.Equal(f.Parent.DefaultDueDaysAfterFreezing, variant.DefaultDueDaysAfterFreezing);
        Assert.Equal(f.Parent.DefaultDueDaysAfterThawing, variant.DefaultDueDaysAfterThawing);
    }

    [Fact]
    public async Task Variant_Tracks_Stock_By_Default()
    {
        var f = MakeFixture();

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.True(variant.TrackStock);
    }

    [Fact]
    public async Task Unit_Override_Replaces_Parent_Unit()
    {
        var f = MakeFixture();

        // Add a second unit that the override will point to.
        var overrideUnit = CatalogUnit.Create(HouseholdId, "L", "Litre", Dimension.Volume, 0.001m);
        f.Units.Items.Add(overrideUnit);

        var result = await f.BuildCommand(unitOverride: overrideUnit.Id.Value).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal(overrideUnit.Id, variant.DefaultUnitId);
    }

    [Fact]
    public async Task Category_Override_Replaces_Parent_Category()
    {
        var f = MakeFixture();

        var overrideCat = Category.Create(HouseholdId, "Beverages");
        f.Categories.Items.Add(overrideCat);

        var result = await f.BuildCommand(categoryOverride: overrideCat.Id.Value).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal(overrideCat.Id, variant.CategoryId);
    }

    [Fact]
    public async Task Location_Override_Replaces_Parent_Location()
    {
        var f = MakeFixture();

        var overrideLoc = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);
        f.Locations.Items.Add(overrideLoc);

        var result = await f.BuildCommand(locationOverride: overrideLoc.Id.Value).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal(overrideLoc.Id, variant.DefaultLocationId);
    }

    [Fact]
    public async Task Parent_HasVariants_Is_Set_To_True_After_First_Variant()
    {
        var f = MakeFixture();
        Assert.False(f.Parent.IsParent);

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(f.Parent.IsParent);
        Assert.True(f.Parent.HasVariants);
    }

    [Fact]
    public async Task Second_Variant_Can_Be_Added_To_Already_Parent_Product()
    {
        var f = MakeFixture();
        f.Parent.SetHasVariants(true, Clock); // simulate an existing variant

        var result = await f.BuildCommand(name: "Semi-skimmed Milk").ExecuteAsync();

        Assert.True(result.IsSuccess);
        var variant = f.Products.Items.Single(p => p.Id == result.Value);
        Assert.Equal(f.Parent.Id, variant.ParentProductId);
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
    public async Task Returns_NotFound_When_Parent_Does_Not_Exist()
    {
        var f = MakeFixture(includeParentInRepo: false); // parent NOT added to repo

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownParentProduct", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_When_Parent_Is_Itself_A_Variant_Depth1_Invariant()
    {
        // The 'parent' in the fixture is already a variant of a grandParent.
        var f = MakeFixture();
        var grandParent = Product.Create(HouseholdId, "GrandParent", f.Unit.Id, Clock);
        grandParent.SetHasVariants(true, Clock);
        f.Parent.MakeVariantOf(grandParent.Id, Clock); // parent is now a variant
        f.Products.Items.Add(grandParent);

        var result = await f.BuildCommand().ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.MaxVariantDepthExceeded", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Duplicate_Variant_Name()
    {
        var f = MakeFixture();
        var existing = Product.Create(HouseholdId, "Whole Milk", f.Unit.Id, Clock);
        f.Products.Items.Add(existing);

        var result = await f.BuildCommand(name: "Whole Milk").ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateProductName", result.Error.Code);
        Assert.Equal(2, f.Products.Items.Count); // no new product added
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Unknown_Unit_Override()
    {
        var f = MakeFixture();
        // Supply an override unit ID that is not in the repo.
        var unknownUnitId = Guid.NewGuid();

        var result = await f.BuildCommand(unitOverride: unknownUnitId).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownUnit", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Unknown_Category_Override()
    {
        var f = MakeFixture();
        var unknownCategoryId = Guid.NewGuid();

        var result = await f.BuildCommand(categoryOverride: unknownCategoryId).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownCategory", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Unknown_Location_Override()
    {
        var f = MakeFixture();
        var unknownLocationId = Guid.NewGuid();

        var result = await f.BuildCommand(locationOverride: unknownLocationId).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownLocation", result.Error.Code);
        Assert.Equal(0, f.Products.SaveChangesCalls);
    }
}
