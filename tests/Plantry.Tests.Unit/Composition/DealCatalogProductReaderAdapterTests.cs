using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Catalog.Application;
using Plantry.Web.Deals;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

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

    [Fact(DisplayName = "ListCandidatesAsync includes parent products, flagged IsParent (plantry-oh27.6)")]
    public async Task ListCandidatesAsync_Includes_Parents()
    {
        var standalone = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        var parent = Product.Create(Household, "Bubly", Unit.Id, SystemClock.Instance);
        parent.SetHasVariants(true, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(standalone);
        products.Items.Add(parent);

        var candidates = await Adapter(products).ListCandidatesAsync();

        Assert.Equal(2, candidates.Count);
        var milk = Assert.Single(candidates, c => c.Id == standalone.Id.Value);
        Assert.False(milk.IsParent);
        var bubly = Assert.Single(candidates, c => c.Id == parent.Id.Value);
        Assert.True(bubly.IsParent);
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

    [Fact(DisplayName = "ForProductsAsync resolves a parent's live variant ids, excluding archived siblings (plantry-oh27.6)")]
    public async Task ForProductsAsync_Resolves_Live_Variant_Ids()
    {
        var parent = Product.Create(Household, "Bubly", Unit.Id, SystemClock.Instance);
        parent.SetHasVariants(true, SystemClock.Instance);
        var orange = Product.Create(Household, "Bubly Orange", Unit.Id, SystemClock.Instance);
        orange.MakeVariantOf(parent.Id, SystemClock.Instance);
        var lime = Product.Create(Household, "Bubly Lime", Unit.Id, SystemClock.Instance);
        lime.MakeVariantOf(parent.Id, SystemClock.Instance);
        var discontinued = Product.Create(Household, "Bubly Grape", Unit.Id, SystemClock.Instance);
        discontinued.MakeVariantOf(parent.Id, SystemClock.Instance);
        discontinued.Archive(SystemClock.Instance);

        var products = new FakeProductRepository();
        products.Items.Add(parent);
        products.Items.Add(orange);
        products.Items.Add(lime);
        products.Items.Add(discontinued);

        var result = await Adapter(products).ForProductsAsync([parent.Id.Value]);

        var info = Assert.Single(result).Value;
        Assert.True(info.IsParent);
        Assert.Equal(2, info.LiveVariantIds.Count);
        Assert.Contains(orange.Id.Value, info.LiveVariantIds);
        Assert.Contains(lime.Id.Value, info.LiveVariantIds);
        Assert.DoesNotContain(discontinued.Id.Value, info.LiveVariantIds);
    }

    [Fact(DisplayName = "ForProductsAsync a leaf product has an empty LiveVariantIds (plantry-oh27.6)")]
    public async Task ForProductsAsync_Leaf_Has_No_Variants()
    {
        var product = Product.Create(Household, "Milk", Unit.Id, SystemClock.Instance);
        var products = new FakeProductRepository();
        products.Items.Add(product);

        var result = await Adapter(products).ForProductsAsync([product.Id.Value]);

        var info = Assert.Single(result).Value;
        Assert.False(info.IsParent);
        Assert.Empty(info.LiveVariantIds);
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
