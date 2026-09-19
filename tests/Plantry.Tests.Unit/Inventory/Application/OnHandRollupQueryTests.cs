using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// Unit tests for <see cref="OnHandRollupQuery"/> (plantry-oh27.2) — the shared parent-aware on-hand
/// read model. Follows the neighbouring <see cref="InventoryQueryServiceTests"/>'s fake pattern.
/// </summary>
public sealed class OnHandRollupQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _location = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _grams = Guid.CreateVersion7();
    private readonly Guid _kilos = Guid.CreateVersion7();
    private readonly Guid _can = Guid.CreateVersion7();
    private readonly Guid _ea = Guid.CreateVersion7();

    private static OnHandRollupQuery Service(
        FakeProductStockRepository stocks, FakeCatalogReadFacade catalog, IQuantityConverter converter, Guid? household) =>
        new(stocks, catalog, new FakeConversionProvider(converter), new FakeTenantContext(household));

    [Fact(DisplayName = "leaf passthrough equals InventoryQueryService.DisplayQuantity")]
    public async Task ForProducts_Leaf_Equals_DisplayQuantity()
    {
        var productId = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), productId, Clock);
        stock.AddStock(500m, _grams, _location, _user, Clock);
        stock.AddStock(2m, _kilos, _location, _user, Clock);
        stocks.Items.Add(stock);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Flour", "Baking", _grams, "g", CanHoldStock: true));
        catalog.UnitCodes[_grams] = "g";
        catalog.UnitCodes[_kilos] = "kg";

        var converter = new FactorQuantityConverter(new() { [(_kilos, _grams)] = 1000m });
        var result = await Service(stocks, catalog, converter, _household).ForProductsAsync([productId]);

        var level = result[productId];
        Assert.Equal(2500m, level.OnHand);
        Assert.Equal(_grams, level.UnitId);
        Assert.Equal("g", level.UnitCode);
        Assert.False(level.IsParent);
        Assert.Empty(level.Variants);
        Assert.Empty(level.UnconvertedVariantIds);
    }

    [Fact(DisplayName = "parent sums two live variants in different units into the parent's default unit")]
    public async Task ForProducts_Parent_Sums_Variants_Across_Units()
    {
        var parentId = Guid.CreateVersion7();
        var variantA = Guid.CreateVersion7();
        var variantB = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stockA = ProductStock.Start(HouseholdId.From(_household), variantA, Clock);
        stockA.AddStock(3m, _can, _location, _user, Clock); // 3 cans -> 6 ea (1 can = 2 ea)
        stocks.Items.Add(stockA);
        var stockB = ProductStock.Start(HouseholdId.From(_household), variantB, Clock);
        stockB.AddStock(4m, _ea, _location, _user, Clock); // 4 ea
        stocks.Items.Add(stockB);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantA, "Bubly Orange", "Drinks", _can, "can", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(variantB, "Bubly Lime", "Drinks", _ea, "ea", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_can] = "can";
        catalog.UnitCodes[_ea] = "ea";

        var converter = new FactorQuantityConverter(new() { [(_can, _ea)] = 2m });
        var result = await Service(stocks, catalog, converter, _household).ForProductsAsync([parentId]);

        var level = result[parentId];
        Assert.True(level.IsParent);
        Assert.Equal(_ea, level.UnitId);
        Assert.Equal("ea", level.UnitCode);
        Assert.Equal(10m, level.OnHand); // 6 + 4
        Assert.Empty(level.UnconvertedVariantIds);
        Assert.Equal(2, level.Variants.Count);
        var a = level.Variants.Single(v => v.VariantId == variantA);
        Assert.Equal(3m, a.OnHand);
        Assert.Equal(_can, a.UnitId);
        Assert.Equal(6m, a.ConvertedOnHand);
        var b = level.Variants.Single(v => v.VariantId == variantB);
        Assert.Equal(4m, b.OnHand);
        Assert.Equal(4m, b.ConvertedOnHand);
    }

    [Fact(DisplayName = "a variant with an incompatible unit lands in UnconvertedVariantIds and is excluded from the sum")]
    public async Task ForProducts_Parent_Excludes_Unconvertible_Variant_From_Sum()
    {
        var parentId = Guid.CreateVersion7();
        var convertible = Guid.CreateVersion7();
        var incompatible = Guid.CreateVersion7();
        var incompatibleUnit = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var goodStock = ProductStock.Start(HouseholdId.From(_household), convertible, Clock);
        goodStock.AddStock(5m, _ea, _location, _user, Clock);
        stocks.Items.Add(goodStock);
        var badStock = ProductStock.Start(HouseholdId.From(_household), incompatible, Clock);
        badStock.AddStock(3m, incompatibleUnit, _location, _user, Clock);
        stocks.Items.Add(badStock);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(convertible, "Bubly Lime", "Drinks", _ea, "ea", CanHoldStock: true, ParentProductId: parentId));
        catalog.Products.Add(new CatalogProductInfo(incompatible, "Bubly Weird", "Drinks", incompatibleUnit, "wut", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_ea] = "ea";
        catalog.UnitCodes[incompatibleUnit] = "wut";

        // No conversion registered between incompatibleUnit and _ea (or vice versa).
        var converter = new FactorQuantityConverter([]);
        var result = await Service(stocks, catalog, converter, _household).ForProductsAsync([parentId]);

        var level = result[parentId];
        Assert.Equal(5m, level.OnHand); // only the convertible variant's contribution
        Assert.Equal([incompatible], level.UnconvertedVariantIds);
        var badContribution = level.Variants.Single(v => v.VariantId == incompatible);
        Assert.Null(badContribution.ConvertedOnHand);
        Assert.Equal(3m, badContribution.OnHand); // still reported, never silently dropped
    }

    [Fact(DisplayName = "an archived variant is excluded from the parent rollup")]
    public async Task ForProducts_Parent_Excludes_Archived_Variant()
    {
        var parentId = Guid.CreateVersion7();
        var liveVariant = Guid.CreateVersion7();
        var archivedVariant = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var liveStock = ProductStock.Start(HouseholdId.From(_household), liveVariant, Clock);
        liveStock.AddStock(5m, _ea, _location, _user, Clock);
        stocks.Items.Add(liveStock);
        var archivedStock = ProductStock.Start(HouseholdId.From(_household), archivedVariant, Clock);
        archivedStock.AddStock(9999m, _ea, _location, _user, Clock);
        stocks.Items.Add(archivedStock);

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(liveVariant, "Bubly Lime", "Drinks", _ea, "ea", CanHoldStock: true, ParentProductId: parentId));
        // Archived variants live in ArchivedProducts (mirrors CatalogReadFacade.ListArchivedProductsAsync),
        // never in ListProductsAsync's active-only result the query builds its variant groups from.
        catalog.ArchivedProducts.Add(new CatalogProductInfo(archivedVariant, "Bubly Discontinued", "Drinks", _ea, "ea",
            CanHoldStock: true, ParentProductId: parentId, IsArchived: true));
        catalog.UnitCodes[_ea] = "ea";

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ForProductsAsync([parentId]);

        var level = result[parentId];
        Assert.Equal(5m, level.OnHand);
        var variant = Assert.Single(level.Variants);
        Assert.Equal(liveVariant, variant.VariantId);
    }

    [Fact(DisplayName = "rule 3: a parent with zero live variants rolls up to 0/IsParent, never omitted")]
    public async Task ForProducts_Parent_With_No_Variants_At_All_Is_Zero_IsParent()
    {
        var parentId = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository(); // no stock rows at all

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.UnitCodes[_ea] = "ea";

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ForProductsAsync([parentId]);

        var level = result[parentId];
        Assert.Equal(0m, level.OnHand);
        Assert.True(level.IsParent);
        Assert.Empty(level.Variants);
    }

    [Fact(DisplayName = "rule 3: a parent whose live variant has no stock row rolls up to 0/IsParent when directly requested")]
    public async Task ForProducts_Parent_With_No_Variant_Stock_Row_Is_Zero_IsParent()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository(); // variant exists in catalog but never had a stock row

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantId, "Bubly Lime", "Drinks", _ea, "ea", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_ea] = "ea";

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ForProductsAsync([parentId]);

        var level = result[parentId];
        Assert.Equal(0m, level.OnHand);
        Assert.True(level.IsParent);
        // rule 4: the live variant still appears in the breakdown even with no stock row.
        var variant = Assert.Single(level.Variants);
        Assert.Equal(variantId, variant.VariantId);
        Assert.Equal(0m, variant.OnHand);
        Assert.Equal(0m, variant.ConvertedOnHand);
    }

    [Fact(DisplayName = "a parent with a live variant that has an (empty) stock row is zero and IsParent")]
    public async Task ForProducts_Parent_With_Empty_Variant_Stock_Row_Is_Zero_IsParent()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        // A stock row exists but has never had any lots added — on-hand is 0, not absent.
        stocks.Items.Add(ProductStock.Start(HouseholdId.From(_household), variantId, Clock));

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentId, "Bubly", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantId, "Bubly Lime", "Drinks", _ea, "ea", CanHoldStock: true, ParentProductId: parentId));
        catalog.UnitCodes[_ea] = "ea";

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ForProductsAsync([parentId]);

        var level = result[parentId];
        Assert.Equal(0m, level.OnHand);
        Assert.True(level.IsParent);
        var variant = Assert.Single(level.Variants);
        Assert.Equal(0m, variant.OnHand);
        Assert.Equal(0m, variant.ConvertedOnHand);
    }

    [Fact(DisplayName = "ForHouseholdAsync includes a parent only when at least one live variant has a stock row")]
    public async Task ForHousehold_Includes_Parent_Only_When_A_Live_Variant_Has_Stock()
    {
        var parentWithStock = Guid.CreateVersion7();
        var variantWithStock = Guid.CreateVersion7();
        var parentWithoutStock = Guid.CreateVersion7();
        var variantWithoutStock = Guid.CreateVersion7();

        var stocks = new FakeProductStockRepository();
        var stock = ProductStock.Start(HouseholdId.From(_household), variantWithStock, Clock);
        stock.AddStock(5m, _ea, _location, _user, Clock);
        stocks.Items.Add(stock);
        // No stock row at all for variantWithoutStock.

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(parentWithStock, "Bubly", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantWithStock, "Bubly Lime", "Drinks", _ea, "ea", CanHoldStock: true, ParentProductId: parentWithStock));
        catalog.Products.Add(new CatalogProductInfo(parentWithoutStock, "Fanta", "Drinks", _ea, "ea", CanHoldStock: false));
        catalog.Products.Add(new CatalogProductInfo(variantWithoutStock, "Fanta Orange", "Drinks", _ea, "ea", CanHoldStock: true, ParentProductId: parentWithoutStock));
        catalog.UnitCodes[_ea] = "ea";

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ForHouseholdAsync();

        Assert.True(result.ContainsKey(parentWithStock));
        Assert.True(result.ContainsKey(variantWithStock));
        Assert.False(result.ContainsKey(parentWithoutStock));
        Assert.False(result.ContainsKey(variantWithoutStock));
    }

    [Fact(DisplayName = "a directly-requested leaf with no stock row reports 0, not absent")]
    public async Task ForProducts_Leaf_With_No_Stock_Row_Is_Zero_Not_Absent()
    {
        var productId = Guid.CreateVersion7();
        var stocks = new FakeProductStockRepository(); // never stocked

        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(productId, "Flour", "Baking", _grams, "g", CanHoldStock: true));
        catalog.UnitCodes[_grams] = "g";

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), _household).ForProductsAsync([productId]);

        var level = result[productId];
        Assert.Equal(0m, level.OnHand);
        Assert.False(level.IsParent);
        Assert.Equal(_grams, level.UnitId);
    }

    [Fact(DisplayName = "no household in context returns an empty result")]
    public async Task ForProducts_Returns_Empty_When_No_Household_In_Context()
    {
        var stocks = new FakeProductStockRepository();
        var catalog = new FakeCatalogReadFacade();

        var result = await Service(stocks, catalog, new IdentityQuantityConverter(), household: null)
            .ForProductsAsync([Guid.CreateVersion7()]);

        Assert.Empty(result);
    }
}
