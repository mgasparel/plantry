using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Application;

/// <summary>
/// L1 unit tests for <see cref="SetDefaultLocationCommand"/> (Take Stock P4-2 / TS-9).
/// Verifies: sets default location; leaves other fields untouched; rejects unknown location;
/// rejects unknown product.
/// </summary>
public sealed class SetDefaultLocationCommandTests
{
    private static readonly HouseholdId HouseholdId = Plantry.SharedKernel.HouseholdId.New();
    private static readonly UnitId UnitId = Plantry.Catalog.Domain.UnitId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private static Product NewProduct(string name = "Flour") =>
        Product.Create(HouseholdId, name, UnitId, Clock);

    private static Location NewLocation(string name = "Pantry") =>
        Location.Create(HouseholdId, name, LocationType.Ambient);

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sets_DefaultLocation_On_Known_Product_With_Known_Location()
    {
        var product = NewProduct();
        var location = NewLocation();

        var products = new FakeProductRepository();
        products.Items.Add(product);

        var locations = new FakeLocationRepository();
        locations.Items.Add(location);

        var result = await new SetDefaultLocationCommand(
            product.Id, location.Id.Value, products, locations, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(location.Id, product.DefaultLocationId);
        Assert.Equal(1, products.SaveChangesCalls);
    }

    [Fact]
    public async Task Does_Not_Touch_Name_Unit_Category_Or_Expiry_Defaults()
    {
        var categoryId = CategoryId.New();
        var product = NewProduct("Oat Milk");
        product.SetCategory(categoryId, Clock);
        product.SetExpiryDefaults(7, 3, 90, 2, Clock);

        var location = NewLocation();

        var products = new FakeProductRepository();
        products.Items.Add(product);

        var locations = new FakeLocationRepository();
        locations.Items.Add(location);

        var result = await new SetDefaultLocationCommand(
            product.Id, location.Id.Value, products, locations, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        // Location updated
        Assert.Equal(location.Id, product.DefaultLocationId);
        // All other fields unchanged
        Assert.Equal("Oat Milk", product.Name);
        Assert.Equal(UnitId, product.DefaultUnitId);
        Assert.Equal(categoryId, product.CategoryId);
        Assert.Equal(7, product.DefaultDueDays);
        Assert.Equal(3, product.DefaultDueDaysAfterOpening);
        Assert.Equal(90, product.DefaultDueDaysAfterFreezing);
        Assert.Equal(2, product.DefaultDueDaysAfterThawing);
    }

    // ── Error paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_NotFound_When_Product_Does_Not_Exist()
    {
        var products = new FakeProductRepository();
        var locations = new FakeLocationRepository();

        var result = await new SetDefaultLocationCommand(
            ProductId.New(), Guid.NewGuid(), products, locations, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
        Assert.Equal(0, products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Unknown_Location()
    {
        var product = NewProduct();

        var products = new FakeProductRepository();
        products.Items.Add(product);

        var locations = new FakeLocationRepository(); // empty — no location seeded

        var result = await new SetDefaultLocationCommand(
            product.Id, Guid.NewGuid(), products, locations, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownLocation", result.Error.Code);
        Assert.Null(product.DefaultLocationId); // unchanged
        Assert.Equal(0, products.SaveChangesCalls);
    }

    [Fact]
    public async Task Rejects_Unknown_Location_Even_When_Product_Already_Has_A_Different_Location()
    {
        var existing = NewLocation("Old Pantry");
        var product = NewProduct();
        product.SetDefaultLocation(existing.Id, Clock);

        var products = new FakeProductRepository();
        products.Items.Add(product);

        var locations = new FakeLocationRepository();
        locations.Items.Add(existing); // existing in repo; the new target is not

        var result = await new SetDefaultLocationCommand(
            product.Id, Guid.NewGuid(), products, locations, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnknownLocation", result.Error.Code);
        Assert.Equal(existing.Id, product.DefaultLocationId); // unchanged
        Assert.Equal(0, products.SaveChangesCalls);
    }
}
