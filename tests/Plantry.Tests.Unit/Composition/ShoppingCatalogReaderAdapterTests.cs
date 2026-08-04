using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Catalog.Application;
using Plantry.Web.Shopping;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="ShoppingCatalogReaderAdapter"/> (plantry-riqy) — the Shopping→Catalog ACL
/// adapter, mirroring the Recipes <c>CatalogProductReaderAdapter</c> pattern. Covers each of the six
/// read methods' core mapping (summary resolve incl. category join, unit-code resolve, stock-eligible
/// candidate list, same-unit conversion pass-through, unit dropdown ordering, and category ordering),
/// plus the two empty-input short-circuits.
/// </summary>
public sealed class ShoppingCatalogReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly CatalogUnit Unit = CatalogUnit.Create(Household, "ea", "Each", Dimension.Count, 1m, isBase: true);

    private static ShoppingCatalogReaderAdapter Adapter(
        FakeProductRepository products, FakeCategoryRepository? categories = null, FakeUnitRepository? units = null) =>
        new(products, categories ?? new FakeCategoryRepository(), units ?? new FakeUnitRepository());

    [Fact(DisplayName = "ResolveSummariesAsync joins the requested product's category name and hue, and only the requested ids")]
    public async Task ResolveSummariesAsync_Joins_Category()
    {
        var category = Category.Create(Household, "Dairy", hue: 120);
        var product = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        product.SetCategory(category.Id, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(product);
        // Decoy: an active product the call does NOT ask for, so Assert.Single can only hold if the
        // adapter joins ListActiveAsync down to the requested ids.
        products.Items.Add(Product.Create(Household, "Bread", Unit.Id, SystemClock.Instance));
        var categories = new FakeCategoryRepository();
        categories.Items.Add(category);

        var result = await Adapter(products, categories).ResolveSummariesAsync([product.Id.Value]);

        var summary = Assert.Single(result).Value;
        Assert.Equal("Milk", summary.Name);
        Assert.Equal("Dairy", summary.CategoryName);
        Assert.Equal(120, summary.CategoryHue);
    }

    [Fact(DisplayName = "ResolveSummariesAsync short-circuits on an empty product id list")]
    public async Task ResolveSummariesAsync_ShortCircuits_On_Empty_Input()
    {
        var result = await Adapter(new FakeProductRepository()).ResolveSummariesAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ResolveUnitCodesAsync resolves codes for the requested unit ids only")]
    public async Task ResolveUnitCodesAsync_Resolves_Requested_Codes()
    {
        var units = new FakeUnitRepository();
        units.Items.Add(Unit);
        var other = CatalogUnit.Create(Household, "L", "Litre", Dimension.Volume, 1m, isBase: true);
        units.Items.Add(other);

        var result = await Adapter(new FakeProductRepository(), units: units).ResolveUnitCodesAsync([Unit.Id.Value]);

        Assert.Equal("ea", Assert.Single(result).Value);
    }

    [Fact(DisplayName = "ResolveUnitCodesAsync short-circuits on an empty unit id list")]
    public async Task ResolveUnitCodesAsync_ShortCircuits_On_Empty_Input()
    {
        var result = await Adapter(new FakeProductRepository()).ResolveUnitCodesAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListProductsAsync excludes parent products and orders by name")]
    public async Task ListProductsAsync_Excludes_Parents_And_Orders_By_Name()
    {
        var b = Product.Create(Household, "Bananas", Unit.Id, SystemClock.Instance);
        var a = Product.Create(Household, "Apples", Unit.Id, SystemClock.Instance);
        var parent = Product.Create(Household, "Bubly", Unit.Id, SystemClock.Instance);
        parent.SetHasVariants(true, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(b);
        products.Items.Add(a);
        products.Items.Add(parent);

        var result = await Adapter(products).ListProductsAsync();

        Assert.Equal(["Apples", "Bananas"], result.Select(p => p.Name));
    }

    [Fact(DisplayName = "TryConvertAsync returns the amount unchanged for a same-unit conversion")]
    public async Task TryConvertAsync_SameUnit_Returns_Amount_Unchanged()
    {
        var product = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(product);

        var result = await Adapter(products).TryConvertAsync(2m, Unit.Id.Value, Unit.Id.Value, product.Id.Value);

        Assert.Equal(2m, result);
    }

    [Fact(DisplayName = "TryConvertAsync returns null when no conversion path exists")]
    public async Task TryConvertAsync_Returns_Null_When_Unresolvable()
    {
        var result = await Adapter(new FakeProductRepository()).TryConvertAsync(
            2m, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact(DisplayName = "ListUnitsAsync returns the household's units ordered for the dropdown")]
    public async Task ListUnitsAsync_Returns_Units()
    {
        var units = new FakeUnitRepository();
        units.Items.Add(Unit);
        var gram = CatalogUnit.Create(Household, "g", "Gram", Dimension.Mass, 1m, isBase: true);
        units.Items.Add(gram);

        var result = await Adapter(new FakeProductRepository(), units: units).ListUnitsAsync();

        Assert.Equal(["g", "ea"], result.Select(u => u.Code));
        Assert.Equal(Unit.Id.Value, result[1].UnitId);
    }

    [Fact(DisplayName = "ListCategoriesAsync returns active categories ordered by sort order then name")]
    public async Task ListCategoriesAsync_Orders_By_SortOrder_Then_Name()
    {
        var zebra = Category.Create(Household, "Zebra", sortOrder: 0);
        var apple = Category.Create(Household, "Apple", sortOrder: 1);
        var bread = Category.Create(Household, "Bread", sortOrder: 1);
        var archived = Category.Create(Household, "Retired", sortOrder: 0);
        archived.Archive(SystemClock.Instance);
        var categories = new FakeCategoryRepository();
        categories.Items.Add(zebra);
        categories.Items.Add(bread);
        categories.Items.Add(apple);
        categories.Items.Add(archived);

        var result = await Adapter(new FakeProductRepository(), categories).ListCategoriesAsync();

        Assert.Equal(["Zebra", "Apple", "Bread"], result.Select(c => c.Name));
    }
}
