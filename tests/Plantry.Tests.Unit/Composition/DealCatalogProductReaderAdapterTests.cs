using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Catalog.Application;
using Plantry.Web.Deals;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="DealCatalogProductReaderAdapter"/> (plantry-riqy) — the Deals→Catalog ACL
/// adapter that validates a deal's resolved product against Catalog's own product/category repositories.
/// Covers existence (incl. archived), the stock-eligible candidate list (parents excluded, DM-19), and
/// the batch resolve (incl. an archived product resolved individually and category-name join).
/// </summary>
public sealed class DealCatalogProductReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly CatalogUnit Unit = CatalogUnit.Create(Household, "ea", "Each", Dimension.Count, 1m, isBase: true);

    private static DealCatalogProductReaderAdapter Adapter(FakeProductRepository products, FakeCategoryRepository? categories = null) =>
        new(products, categories ?? new FakeCategoryRepository());

    [Fact(DisplayName = "ExistsAsync is true for a live, non-archived product")]
    public async Task ExistsAsync_True_For_Live_Product()
    {
        var product = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(product);

        Assert.True(await Adapter(products).ExistsAsync(product.Id.Value));
    }

    [Fact(DisplayName = "ExistsAsync is false for an archived product")]
    public async Task ExistsAsync_False_For_Archived_Product()
    {
        var product = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        product.Archive(SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(product);

        Assert.False(await Adapter(products).ExistsAsync(product.Id.Value));
    }

    [Fact(DisplayName = "ExistsAsync is false for an unknown product id")]
    public async Task ExistsAsync_False_For_Unknown_Product()
    {
        Assert.False(await Adapter(new FakeProductRepository()).ExistsAsync(Guid.NewGuid()));
    }

    [Fact(DisplayName = "ListCandidatesAsync excludes parent products that cannot hold stock (DM-19)")]
    public async Task ListCandidatesAsync_Excludes_Parents()
    {
        var standalone = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        var parent = Product.Create(Household, "Bubly", Unit.Id, SystemClock.Instance);
        parent.SetHasVariants(true, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(standalone);
        products.Items.Add(parent);

        var candidates = await Adapter(products).ListCandidatesAsync();

        var candidate = Assert.Single(candidates);
        Assert.Equal(standalone.Id.Value, candidate.Id);
        Assert.Equal("Milk", candidate.Name);
    }

    [Fact(DisplayName = "ForProductsAsync short-circuits on an empty id list")]
    public async Task ForProductsAsync_ShortCircuits_On_Empty_Input()
    {
        var result = await Adapter(new FakeProductRepository()).ForProductsAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ForProductsAsync resolves an active product with its category name")]
    public async Task ForProductsAsync_Resolves_Active_Product_With_Category()
    {
        var category = Category.Create(Household, "Dairy");
        var product = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        product.SetCategory(category.Id, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(product);
        var categories = new FakeCategoryRepository();
        categories.Items.Add(category);

        var result = await Adapter(products, categories).ForProductsAsync([product.Id.Value]);

        var info = Assert.Single(result).Value;
        Assert.Equal("Milk", info.Name);
        Assert.Equal("Dairy", info.CategoryName);
    }

    [Fact(DisplayName = "ForProductsAsync still resolves an archived product by individual FindAsync fallback")]
    public async Task ForProductsAsync_Resolves_Archived_Product_Individually()
    {
        var product = Product.Create(Household, "Discontinued Soda", Unit.Id, SystemClock.Instance);
        product.Archive(SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(product);

        var result = await Adapter(products).ForProductsAsync([product.Id.Value]);

        var info = Assert.Single(result).Value;
        Assert.Equal("Discontinued Soda", info.Name);
        Assert.Null(info.CategoryName);
    }

    [Fact(DisplayName = "ForProductsAsync omits an id that resolves to no product at all")]
    public async Task ForProductsAsync_Omits_Fully_Unknown_Id()
    {
        // Decoy: a live product the call does NOT ask for, so Assert.Empty can only hold if the
        // adapter filters ListActiveAsync down to the requested ids.
        var products = new FakeProductRepository();
        products.Items.Add(Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance));

        var result = await Adapter(products).ForProductsAsync([Guid.NewGuid()]);

        Assert.Empty(result);
    }
}
