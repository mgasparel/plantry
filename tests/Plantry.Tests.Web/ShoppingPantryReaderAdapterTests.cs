using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Planning.Application;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Shopping;

namespace Plantry.Tests.Web;

/// <summary>
/// L2 unit tests for <see cref="ShoppingPantryReaderAdapter"/> — the Shopping→Inventory
/// anti-corruption read port (plantry-juh). Verifies on-hand quantity aggregation, IsLow
/// derivation, tenant scoping, and that the adapter stays behind the port boundary
/// (Shopping never sees raw Inventory domain types).
///
/// Tests live in Plantry.Tests.Web because the adapter is in Plantry.Web; they do NOT use
/// WebApplicationFactory — they instantiate the adapter directly with in-memory fakes.
/// </summary>
public sealed class ShoppingPantryReaderAdapterTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly Guid HouseholdGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdGuid);

    private static readonly Guid MilkId  = Guid.Parse("11111111-1111-1111-1111-000000000001");
    private static readonly Guid FlourId = Guid.Parse("11111111-1111-1111-1111-000000000002");
    private static readonly Guid LitreId = Guid.Parse("22222222-2222-2222-2222-000000000001");
    private static readonly Guid GramId  = Guid.Parse("22222222-2222-2222-2222-000000000002");
    private static readonly Guid PoundId = Guid.Parse("22222222-2222-2222-2222-000000000003");
    private static readonly Guid EachId  = Guid.Parse("22222222-2222-2222-2222-000000000004");

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Backs every <see cref="BuildAdapter"/> call in this test class by default — the
    /// low-stock threshold record (plantry-oh27.1), populated per test via
    /// <see cref="MakeStockWithLotAndThreshold"/>/<see cref="MakeStockWithThresholdNoLots"/>.</summary>
    private readonly FakeLowStockRuleRepository _rules = new();

    private ShoppingPantryReaderAdapter BuildAdapter(
        IProductStockRepository stocks,
        ICatalogReadFacade catalog,
        ITenantContext? tenantCtx = null,
        ILowStockRuleRepository? rules = null,
        IProductConversionProvider? conversions = null)
    {
        var tenant = tenantCtx ?? new FakePantryTenantContext(HouseholdGuid);
        var conversionProvider = conversions ?? new FakePantryConversionProvider();
        // The real OnHandRollupQuery over the same test doubles (mirrors production DI, Program.cs) —
        // the adapter and the rollup query share the exact same repositories/facades/tenant so this
        // exercises the real parent-fold arithmetic rather than a hand-rolled fake.
        var rollup = new OnHandRollupQuery(stocks, catalog, conversionProvider, tenant);
        return new ShoppingPantryReaderAdapter(stocks, rules ?? _rules, catalog, rollup, tenant);
    }

    private static ProductStock MakeStock(Guid productId) =>
        ProductStock.Start(Household, productId, Clock);

    private static ProductStock MakeStockWithLot(Guid productId, decimal quantity, Guid unitId)
    {
        var stock = ProductStock.Start(Household, productId, Clock);
        stock.AddStock(quantity, unitId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        return stock;
    }

    /// <summary>Stock with an active lot and a low stock threshold set (for running-low tests) — the
    /// threshold is recorded as a <see cref="LowStockRule"/> in <see cref="_rules"/>, not on the stock.</summary>
    private ProductStock MakeStockWithLotAndThreshold(Guid productId, decimal quantity, Guid unitId, decimal threshold)
    {
        var stock = MakeStockWithLot(productId, quantity, unitId);
        _rules.Items.Add(LowStockRule.Create(Household, productId, threshold, Clock));
        return stock;
    }

    /// <summary>Stock with a threshold set but no active lots (out, but with a threshold configured).</summary>
    private ProductStock MakeStockWithThresholdNoLots(Guid productId, decimal threshold)
    {
        var stock = ProductStock.Start(Household, productId, Clock);
        _rules.Items.Add(LowStockRule.Create(Household, productId, threshold, Clock));
        return stock;
    }

    // ── Core enrichment ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GetStockLevels — product with active lot: OnHand = lot quantity, IsLow = false")]
    public async Task GetStockLevels_ProductWithLot_OnHandPopulated()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 2m, LitreId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(MilkId, level.ProductId);
        Assert.Equal(2m, level.OnHand);
        Assert.Equal("L", level.UnitCode);
        Assert.False(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — out product, no threshold: OnHand = 0, IsLow = false (out is not running low)")]
    public async Task GetStockLevels_ProductWithNoLots_OnHandZeroIsLowFalse()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStock(MilkId)); // no lots, no threshold

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(0m, level.OnHand);
        // IsLow is running-low only (0 < onHand ≤ threshold). Out (onHand = 0) is NOT running low.
        Assert.False(level.IsLow);
    }

    // ── Tri-state IsLow computation (plantry-43y): in-stock / running-low / out ─

    [Fact(DisplayName = "GetStockLevels — in-stock above threshold: IsLow = false")]
    public async Task GetStockLevels_AboveThreshold_IsLowFalse()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 10m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(10m, level.OnHand);
        Assert.False(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — running low (0 < onHand < threshold): IsLow = true")]
    public async Task GetStockLevels_BelowThreshold_IsLowTrue()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 2m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(2m, level.OnHand);
        Assert.True(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — onHand exactly at threshold: IsLow = true (inclusive boundary)")]
    public async Task GetStockLevels_AtThreshold_IsLowTrue()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 3m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(3m, level.OnHand);
        Assert.True(level.IsLow);
    }

    [Fact(DisplayName = "GetStockLevels — out with threshold set: OnHand = 0, IsLow = false (out beats low)")]
    public async Task GetStockLevels_OutWithThreshold_IsLowFalse()
    {
        var stocks = new FakePantryStockRepository();
        // Threshold set but no active lots: IsRunningLow(0) alone would be true (0 ≤ 3); the adapter's
        // onHand > 0 guard forces IsLow = false so out and low never both fire.
        stocks.Add(MakeStockWithThresholdNoLots(MilkId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(0m, level.OnHand);
        Assert.False(level.IsLow);
    }

    // ── Restock-candidate predicate (plantry-43y): GetLowStockProductsAsync = low ∪ out ─

    [Fact(DisplayName = "GetLowStockProducts — includes running-low, out, and excludes in-stock")]
    public async Task GetLowStockProducts_ReturnsLowAndOut_ExcludesInStock()
    {
        var eggsId = Guid.Parse("11111111-1111-1111-1111-000000000003");

        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 2m, LitreId, threshold: 3m));   // running low
        stocks.Add(MakeStockWithThresholdNoLots(FlourId, threshold: 500m));                        // out
        stocks.Add(MakeStockWithLotAndThreshold(eggsId, quantity: 10m, LitreId, threshold: 3m));   // in-stock

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");
        catalog.AddProduct(FlourId, defaultUnitId: GramId, defaultUnitCode: "g");
        catalog.AddProduct(eggsId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        Assert.Equal(2, result.Count);
        var milk = Assert.Single(result, l => l.ProductId == MilkId);
        Assert.True(milk.IsLow);   // running low
        var flour = Assert.Single(result, l => l.ProductId == FlourId);
        Assert.False(flour.IsLow); // out — surfaced via OnHand ≤ 0, not IsLow
        Assert.Equal(0m, flour.OnHand);
        Assert.DoesNotContain(result, l => l.ProductId == eggsId); // in-stock excluded
    }

    // ── Produced-product exclusion (plantry-sn6v): a recipe yield/leftover is never a buy suggestion ─

    [Fact(DisplayName = "GetLowStockProducts — excludes a produced product even when it reads as out")]
    public async Task GetLowStockProducts_ExcludesProducedProduct_WhenOut()
    {
        var leftoversId = Guid.Parse("11111111-1111-1111-1111-000000000004");

        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStock(leftoversId)); // no lots — reads as out (OnHand ≤ 0)

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(leftoversId, defaultUnitId: EachId, defaultUnitCode: "ea", isProduced: true);

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        Assert.DoesNotContain(result, l => l.ProductId == leftoversId);
    }

    [Fact(DisplayName = "GetLowStockProducts — excludes a produced product even when running low")]
    public async Task GetLowStockProducts_ExcludesProducedProduct_WhenRunningLow()
    {
        var leftoversId = Guid.Parse("11111111-1111-1111-1111-000000000005");

        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(leftoversId, quantity: 1m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(leftoversId, defaultUnitId: LitreId, defaultUnitCode: "L", isProduced: true);

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        Assert.DoesNotContain(result, l => l.ProductId == leftoversId);
    }

    [Fact(DisplayName = "GetLowStockProducts — an ordinary purchased product going out still appears (regression guard on the exclusion)")]
    public async Task GetLowStockProducts_OrdinaryProduct_StillIncluded_NotOverExcluded()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithThresholdNoLots(MilkId, threshold: 3m)); // out, ordinary purchased product

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L", isProduced: false);

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        Assert.Contains(result, l => l.ProductId == MilkId);
    }

    [Fact(DisplayName = "GetStockLevels — a produced product's on-hand level still resolves (exclusion is restock-candidate-only)")]
    public async Task GetStockLevels_ProducedProduct_StillResolves()
    {
        // The exclusion is specific to GetLowStockProductsAsync's restock-candidate semantics —
        // GetStockLevelsAsync (used by e.g. a product detail page asking about a specific product)
        // must still report a produced product's real on-hand level.
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 2m, LitreId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L", isProduced: true);

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(2m, level.OnHand);
    }

    [Fact(DisplayName = "GetStockLevels — product with multiple lots: OnHand is the sum")]
    public async Task GetStockLevels_MultipleLots_OnHandIsSumOfActiveLots()
    {
        var stocks = new FakePantryStockRepository();
        var stock = ProductStock.Start(Household, MilkId, Clock);
        stock.AddStock(1m, LitreId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stock.AddStock(2m, LitreId, locationId: Guid.NewGuid(), userId: Guid.NewGuid(), Clock);
        stocks.Add(stock);

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Equal(3m, result[MilkId].OnHand);
    }

    [Fact(DisplayName = "GetStockLevels — product not stocked at all: omitted from result")]
    public async Task GetStockLevels_ProductNeverStocked_OmittedFromResult()
    {
        var stocks = new FakePantryStockRepository(); // empty
        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetStockLevels — multiple products: each resolved independently")]
    public async Task GetStockLevels_MultipleProducts_EachResolvedIndependently()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 1m, LitreId));
        stocks.Add(MakeStockWithLot(FlourId, 500m, GramId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");
        catalog.AddProduct(FlourId, defaultUnitId: GramId, defaultUnitCode: "g");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId, FlourId]);

        Assert.Equal(2, result.Count);
        Assert.Equal(1m, result[MilkId].OnHand);
        Assert.Equal("L", result[MilkId].UnitCode);
        Assert.Equal(500m, result[FlourId].OnHand);
        Assert.Equal("g", result[FlourId].UnitCode);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GetStockLevels — empty product list: returns empty dictionary without calling repository")]
    public async Task GetStockLevels_EmptyProductList_ReturnsEmpty()
    {
        var stocks = new FakePantryStockRepository();
        var catalog = new FakePantryCatalogFacade();
        var adapter = BuildAdapter(stocks, catalog);

        var result = await adapter.GetStockLevelsAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetStockLevels — no tenant context: returns empty dictionary (no household to query)")]
    public async Task GetStockLevels_NoTenant_ReturnsEmpty()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 1m, LitreId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog, new FakePantryTenantContext(null));
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetStockLevels — product archived from catalog: omitted from result")]
    public async Task GetStockLevels_ProductNotInCatalog_OmittedFromResult()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 1m, LitreId));

        // Catalog has no entry for MilkId — product was archived.
        var catalog = new FakePantryCatalogFacade();

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        Assert.Empty(result);
    }

    // ── Unconvertible-unit fallback (plantry-2hfi) ───────────────────────────────
    // Regression coverage for "3 lbs of Onion Yellow reads as out": a lot whose unit cannot
    // convert to the product's default unit (e.g. lb lot on an "ea" product, no product-specific
    // factor) must never silently contribute 0 to OnHand. The adapter shares
    // InventoryQueryService.DisplayQuantity's fallback-to-lot-unit semantics with the pantry list
    // so the two contexts can't disagree about the same on-hand data.

    [Fact(DisplayName = "GetStockLevels — lot unit fails to convert to default unit: falls back to the lot's own quantity/unit rather than reporting zero")]
    public async Task GetStockLevels_UnconvertibleUnit_FallsBackToLotUnit_NotZero()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 3m, PoundId)); // "Onion Yellow" analogue: 3 lb lot

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: EachId, defaultUnitCode: "ea");
        catalog.AddUnitCode(PoundId, "lb");

        var adapter = BuildAdapter(stocks, catalog, rules: new FakeLowStockRuleRepository(), conversions: new FakeMismatchConversionProvider());
        var result = await adapter.GetStockLevelsAsync([MilkId]);

        var level = Assert.Single(result).Value;
        Assert.Equal(3m, level.OnHand);
        Assert.Equal("lb", level.UnitCode);
        Assert.False(level.IsLow); // no threshold set — merely unconvertible, not low
    }

    [Fact(DisplayName = "GetLowStockProducts — lot unit fails to convert to default unit: NOT classified as 'out'")]
    public async Task GetLowStockProducts_UnconvertibleUnit_NotClassifiedOut()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(MilkId, 3m, PoundId));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: EachId, defaultUnitCode: "ea");
        catalog.AddUnitCode(PoundId, "lb");

        var adapter = BuildAdapter(stocks, catalog, rules: new FakeLowStockRuleRepository(), conversions: new FakeMismatchConversionProvider());
        var result = await adapter.GetLowStockProductsAsync();

        // No threshold set, and the fallback quantity (3 lb) is positive — this product is neither
        // running low nor out, so it must not surface as a restock candidate at all.
        Assert.DoesNotContain(result, l => l.ProductId == MilkId);
    }

    // ── Parent fold (plantry-oh27.3): "The rule" scenarios from the bead description ────────────────

    private static readonly Guid ParentId = Guid.Parse("33333333-3333-3333-3333-000000000001");
    private static readonly Guid OrangeId = Guid.Parse("33333333-3333-3333-3333-000000000002");
    private static readonly Guid LimeId = Guid.Parse("33333333-3333-3333-3333-000000000003");

    [Fact(DisplayName = "GetLowStockProducts — parent with a rule and two low variants: one Tier-1 parent row, no variant rows")]
    public async Task GetLowStockProducts_ParentWithRule_TwoLowVariants_OneParentRow()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(OrangeId, 1m, EachId)); // variant, no own rule
        stocks.Add(MakeStockWithLot(LimeId, 1m, EachId));   // variant, no own rule

        var catalog = new FakePantryCatalogFacade();
        catalog.AddParentProduct(ParentId, "Bubly", EachId, "ea");
        catalog.AddNamedProduct(OrangeId, "Bubly Orange", EachId, "ea", ParentId);
        catalog.AddNamedProduct(LimeId, "Bubly Lime", EachId, "ea", ParentId);

        _rules.Items.Add(LowStockRule.Create(Household, ParentId, threshold: 4m, Clock)); // parent-only rule

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        var row = Assert.Single(result);
        Assert.Equal(ParentId, row.ProductId);
        Assert.True(row.IsParent);
        Assert.Equal(2m, row.OnHand); // 1 + 1, both "ea"
        Assert.True(row.IsLow);       // 2 <= threshold 4
    }

    [Fact(DisplayName = "GetLowStockProducts — a variant with its own rule appears alongside the parent's row")]
    public async Task GetLowStockProducts_VariantWithOwnRule_AppearsAlongsideParent()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(OrangeId, 1m, EachId)); // has its own rule below
        stocks.Add(MakeStockWithLot(LimeId, 1m, EachId));   // no own rule — represented by parent

        var catalog = new FakePantryCatalogFacade();
        catalog.AddParentProduct(ParentId, "Bubly", EachId, "ea");
        catalog.AddNamedProduct(OrangeId, "Bubly Orange", EachId, "ea", ParentId);
        catalog.AddNamedProduct(LimeId, "Bubly Lime", EachId, "ea", ParentId);

        _rules.Items.Add(LowStockRule.Create(Household, ParentId, threshold: 4m, Clock));
        _rules.Items.Add(LowStockRule.Create(Household, OrangeId, threshold: 3m, Clock)); // own rule, running low at 1

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        Assert.Equal(2, result.Count);
        var orangeRow = Assert.Single(result, r => r.ProductId == OrangeId);
        Assert.False(orangeRow.IsParent);
        Assert.True(orangeRow.IsLow);
        var parentRow = Assert.Single(result, r => r.ProductId == ParentId);
        Assert.True(parentRow.IsParent);
        Assert.Equal(2m, parentRow.OnHand); // still sums BOTH live variants
    }

    [Fact(DisplayName = "GetLowStockProducts — parent without a rule, all live variants out: Tier-3 parent row")]
    public async Task GetLowStockProducts_ParentWithoutRule_AllVariantsOut_TierThreeParentRow()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStock(OrangeId)); // no lots
        stocks.Add(MakeStock(LimeId));   // no lots

        var catalog = new FakePantryCatalogFacade();
        catalog.AddParentProduct(ParentId, "Bubly", EachId, "ea");
        catalog.AddNamedProduct(OrangeId, "Bubly Orange", EachId, "ea", ParentId);
        catalog.AddNamedProduct(LimeId, "Bubly Lime", EachId, "ea", ParentId);
        // No rule anywhere.

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        var row = Assert.Single(result);
        Assert.Equal(ParentId, row.ProductId);
        Assert.True(row.IsParent);
        Assert.Equal(0m, row.OnHand);
        Assert.False(row.IsLow); // out, not "running low"
        Assert.False(row.HasLowStockThreshold);
    }

    [Fact(DisplayName = "GetLowStockProducts — parent whose only live variant is unconvertible: NOT classified as out (plantry-2hfi extended to parents)")]
    public async Task GetLowStockProducts_ParentWithUnconvertibleVariant_NotClassifiedOut()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLot(OrangeId, 3m, PoundId)); // positive stock, but "lb" cannot convert to the parent's "ea"

        var catalog = new FakePantryCatalogFacade();
        catalog.AddParentProduct(ParentId, "Bubly", EachId, "ea");
        catalog.AddNamedProduct(OrangeId, "Bubly Orange", PoundId, "lb", ParentId);
        catalog.AddUnitCode(PoundId, "lb");
        // No rule anywhere.

        var adapter = BuildAdapter(stocks, catalog, conversions: new FakeMismatchConversionProvider());
        var result = await adapter.GetLowStockProductsAsync();

        // The variant genuinely has 3 lb on hand — the parent's rolled-up OnHand reads 0 only because
        // the "lb" -> "ea" conversion failed, not because the household is actually out. Must not
        // surface as a Tier-3 "out" restock candidate.
        Assert.DoesNotContain(result, r => r.ProductId == ParentId);
    }

    [Fact(DisplayName = "GetFrequentStapleProducts — a produced parent is excluded even with frequently-purchased ruleless variants (plantry-sn6v)")]
    public async Task GetFrequentStapleProducts_ProducedParent_Excluded()
    {
        var today = new DateOnly(2026, 9, 19);

        var orangeStock = ProductStock.Start(Household, OrangeId, Clock);
        orangeStock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, purchasedAt: today.AddDays(-7));
        orangeStock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, purchasedAt: today.AddDays(-21));
        var limeStock = ProductStock.Start(Household, LimeId, Clock);
        limeStock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, purchasedAt: today.AddDays(-14));

        var stocks = new FakePantryStockRepository();
        stocks.Add(orangeStock);
        stocks.Add(limeStock);

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProducedParentProduct(ParentId, "Frozen Dinner Portions", EachId, "ea");
        catalog.AddNamedProduct(OrangeId, "Frozen Dinner Portion A", EachId, "ea", ParentId);
        catalog.AddNamedProduct(LimeId, "Frozen Dinner Portion B", EachId, "ea", ParentId);

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetFrequentStapleProductsAsync(today);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetFrequentStapleProducts — parent without a rule, variants bought weekly in rotation: Tier-2 parent")]
    public async Task GetFrequentStapleProducts_ParentWithoutRule_VariantsBoughtWeekly_TierTwoParent()
    {
        var today = new DateOnly(2026, 9, 19);

        // Union of purchase dates across the two variants satisfies the frequent-staple predicate even
        // though neither variant alone was purchased 3+ times: orange twice, lime once, 3 distinct dates.
        var orangeStock = ProductStock.Start(Household, OrangeId, Clock);
        orangeStock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, purchasedAt: today.AddDays(-7));
        orangeStock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, purchasedAt: today.AddDays(-21));
        var limeStock = ProductStock.Start(Household, LimeId, Clock);
        limeStock.AddStock(1m, EachId, Guid.NewGuid(), Guid.NewGuid(), Clock, purchasedAt: today.AddDays(-14));

        var stocks = new FakePantryStockRepository();
        stocks.Add(orangeStock);
        stocks.Add(limeStock);

        var catalog = new FakePantryCatalogFacade();
        catalog.AddParentProduct(ParentId, "Bubly", EachId, "ea");
        catalog.AddNamedProduct(OrangeId, "Bubly Orange", EachId, "ea", ParentId);
        catalog.AddNamedProduct(LimeId, "Bubly Lime", EachId, "ea", ParentId);

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetFrequentStapleProductsAsync(today);

        var row = Assert.Single(result);
        Assert.Equal(ParentId, row.ProductId);
        Assert.True(row.IsParent);
        Assert.Equal(3m, row.OnHand); // 2 (orange) + 1 (lime)
    }

    [Fact(DisplayName = "GetLowStockProducts — leaf with no parent is unaffected by the fold")]
    public async Task GetLowStockProducts_LeafWithNoParent_Unaffected()
    {
        var stocks = new FakePantryStockRepository();
        stocks.Add(MakeStockWithLotAndThreshold(MilkId, quantity: 1m, LitreId, threshold: 3m));

        var catalog = new FakePantryCatalogFacade();
        catalog.AddProduct(MilkId, defaultUnitId: LitreId, defaultUnitCode: "L");

        var adapter = BuildAdapter(stocks, catalog);
        var result = await adapter.GetLowStockProductsAsync();

        var row = Assert.Single(result);
        Assert.Equal(MilkId, row.ProductId);
        Assert.False(row.IsParent);
        Assert.True(row.IsLow);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class FakePantryTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakePantryStockRepository : IProductStockRepository
{
    private readonly List<ProductStock> _stocks = [];

    public void Add(ProductStock stock) => _stocks.Add(stock);

    public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Where(s => s.HouseholdId == householdId).ToList());

    public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        FindAsync(householdId, productId, ct);

    public Task AddAsync(ProductStock stock, CancellationToken ct = default) { _stocks.Add(stock); return Task.CompletedTask; }
    public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default) { _stocks.Add(stock); return Task.FromResult(true); }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_stocks.Any(s => s.HouseholdId == householdId));

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        await work(ct);
}

file sealed class FakePantryCatalogFacade : ICatalogReadFacade
{
    private readonly List<CatalogProductInfo> _products = [];
    private readonly Dictionary<Guid, string> _unitCodes = [];

    public void AddProduct(Guid id, Guid defaultUnitId, string defaultUnitCode, bool isProduced = false) =>
        _products.Add(new CatalogProductInfo(id, "Product", null, defaultUnitId, defaultUnitCode, CanHoldStock: true, IsProduced: isProduced));

    public void AddNamedProduct(Guid id, string name, Guid defaultUnitId, string defaultUnitCode, Guid? parentProductId = null) =>
        _products.Add(new CatalogProductInfo(id, name, null, defaultUnitId, defaultUnitCode, CanHoldStock: true, ParentProductId: parentProductId));

    /// <summary>Registers a parent product — never holds stock (epic constraint, plantry-oh27).</summary>
    public void AddParentProduct(Guid id, string name, Guid defaultUnitId, string defaultUnitCode) =>
        _products.Add(new CatalogProductInfo(id, name, null, defaultUnitId, defaultUnitCode, CanHoldStock: false));

    /// <summary>Registers a produced parent product (plantry-sn6v — "made at home, not bought") — never
    /// holds stock, and never a restock candidate regardless of how its variants read.</summary>
    public void AddProducedParentProduct(Guid id, string name, Guid defaultUnitId, string defaultUnitCode) =>
        _products.Add(new CatalogProductInfo(id, name, null, defaultUnitId, defaultUnitCode, CanHoldStock: false, IsProduced: true));

    /// <summary>Registers a unit id → code mapping for <see cref="GetUnitCodesAsync"/>. Needed when a
    /// test exercises the DisplayQuantity fallback-to-lot-unit path (plantry-2hfi), which reports the
    /// lot's own unit code rather than the product's default unit code.</summary>
    public void AddUnitCode(Guid unitId, string code) => _unitCodes[unitId] = code;

    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.SingleOrDefault(p => p.Id == productId));

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>(_products);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(_unitCodes);

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>Identity converter: same-unit and cross-unit both pass through (tests use matching units).</summary>
file sealed class FakePantryConversionProvider : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new IdentityConverter());

    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}

/// <summary>Converter that only succeeds when the from/to unit ids are identical and fails otherwise —
/// simulates a real <see cref="IQuantityConverter"/> when no product-specific factor bridges two
/// incompatible dimensions (e.g. lb -&gt; ea). Exercises the DisplayQuantity fallback-to-lot-unit path
/// (plantry-2hfi: a lot that merely fails unit conversion must never read as "out").</summary>
file sealed class FakeMismatchConversionProvider : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new MismatchConverter());

    private sealed class MismatchConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) =>
            fromUnitId == toUnitId
                ? amount
                : Result<decimal>.Failure(Error.Custom("Test.NoConversion", "No conversion factor between these units."));
    }
}
