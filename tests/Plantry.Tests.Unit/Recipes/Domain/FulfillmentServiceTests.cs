using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using InventoryProductStock = Plantry.Recipes.Application.ProductStock;

namespace Plantry.Tests.Unit.Recipes.Domain;

/// <summary>
/// L1 tests for <see cref="FulfillmentService"/> — all dependencies faked in-memory.
/// Covers: untracked always satisfied, InStock/Low/Missing thresholds, parent/variant rollup (DM-19),
/// expiring-soon flag (J1/J3 ≤4 days), and serving-scale changes status.
/// </summary>
public sealed class FulfillmentServiceTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly DateOnly Today = new(2026, 6, 14);

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeInventoryStockReader : IInventoryStockReader
    {
        private readonly Dictionary<Guid, InventoryProductStock> _stock = [];

        public void Add(Guid productId, decimal available, Guid defaultUnitId, DateOnly? soonestExpiry = null) =>
            _stock[productId] = new InventoryProductStock(productId, available, defaultUnitId, soonestExpiry);

        public Task<InventoryProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_stock.GetValueOrDefault(productId));

        public Task<IReadOnlyDictionary<Guid, InventoryProductStock>> FindStockBatchAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, InventoryProductStock> result = productIds
                .Where(_stock.ContainsKey)
                .ToDictionary(id => id, id => _stock[id]);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCatalogProductReader : ICatalogProductReader
    {
        private readonly Dictionary<Guid, CatalogProduct> _products = [];

        public void Add(CatalogProduct product) => _products[product.Id] = product;

        public CatalogProduct AddUntracked(string name = "Salt")
        {
            var p = new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: false,
                DefaultUnitId: Guid.CreateVersion7(), ParentProductId: null, IsParent: false, VariantProductIds: []);
            _products[p.Id] = p;
            return p;
        }

        public CatalogProduct AddTrackedLeaf(Guid defaultUnitId, string name = "Flour")
        {
            var p = new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: true,
                defaultUnitId, null, false, []);
            _products[p.Id] = p;
            return p;
        }

        public CatalogProduct AddParent(IReadOnlyList<Guid> variantIds, string name = "Milk")
        {
            var p = new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: true,
                DefaultUnitId: Guid.CreateVersion7(), ParentProductId: null, IsParent: true, variantIds);
            _products[p.Id] = p;
            return p;
        }

        public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_products.GetValueOrDefault(productId));

        public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);

        public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CatalogProductSummary>>(new Dictionary<Guid, CatalogProductSummary>());

        public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
            IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);

        public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);

        public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
    }

    /// <summary>Identity converter — assumes ingredient unit == product default unit for simplicity.</summary>
    private sealed class IdentityUnitConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
        {
            // Same unit: identity.
            if (fromUnitId == toUnitId)
                return Task.FromResult(Result<decimal>.Success(amount));
            // Fixed conversions map registered explicitly.
            return Task.FromResult(Result<decimal>.Failure(
                Error.Custom("Catalog.NoConversionPath", "No conversion path.")));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public FakeInventoryStockReader Stock { get; } = new();
        public FakeCatalogProductReader Catalog { get; } = new();
        public IdentityUnitConverter Converter { get; } = new();

        /// <summary>The "expiring soon" horizon the fake reader returns; defaults to 4 so the
        /// existing boundary tests exercise a 4-day window. Set before reading <see cref="Service"/>.</summary>
        public int HorizonDays { get; set; } = 4;

        public FulfillmentService Service => new(Stock, Catalog, Converter, new FakeHorizonReader(HorizonDays));

        private sealed class FakeHorizonReader(int days) : IExpiringSoonHorizonReader
        {
            public Task<int> GetDaysAsync(CancellationToken ct = default) => Task.FromResult(days);
        }
    }

    /// <summary>Builds a minimal recipe with a single tracked ingredient at the given qty/unit.</summary>
    private static Recipe BuildRecipe(Guid productId, decimal quantity, Guid unitId, int defaultServings = 4)
    {
        var recipe = Recipe.Create(Household, "Test Recipe", defaultServings, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, quantity, unitId, null, 0)], Clock);
        return recipe;
    }

    // ── Untracked always satisfied (C12) ──────────────────────────────────────

    [Fact]
    public async Task Untracked_Ingredient_Is_Always_Untracked_Status()
    {
        var h = new Harness();
        var salt = h.Catalog.AddUntracked();
        // No stock entry for salt — and none should be needed.

        // Untracked staple: null qty/unit allowed by R5 when track_stock = false.
        var recipe = Recipe.Create(Household, "Omelette", 2, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(salt.Id, null, null, null, 0)], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 2, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Untracked, line.Status);
        Assert.Null(line.ExpiresWithinDays);
        Assert.Null(line.AvailableQuantity);
        Assert.True(result.Overall.FullyCookable);
    }

    // ── InStock / Low / Missing thresholds ────────────────────────────────────

    [Fact]
    public async Task Tracked_Ingredient_IsInStock_When_Available_Meets_Required()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var flour = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(flour.Id, available: 500m, unit);

        var recipe = BuildRecipe(flour.Id, 500m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.InStock, line.Status);
        Assert.True(result.Overall.FullyCookable);
    }

    [Fact]
    public async Task Tracked_Ingredient_IsLow_When_Available_Is_Between_Zero_And_Required()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var flour = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(flour.Id, available: 250m, unit); // only half

        var recipe = BuildRecipe(flour.Id, 500m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Low, line.Status);
        Assert.Equal(250m, line.AvailableQuantity);
        Assert.False(result.Overall.FullyCookable);
        Assert.Equal(0, result.Overall.MissingCount);
        Assert.Equal(1, result.Overall.LowCount);
    }

    [Fact]
    public async Task Tracked_Ingredient_IsMissing_When_No_Stock_Record()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var flour = h.Catalog.AddTrackedLeaf(unit);
        // No stock added.

        var recipe = BuildRecipe(flour.Id, 500m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Missing, line.Status);
        Assert.False(result.Overall.FullyCookable);
        Assert.Equal(1, result.Overall.MissingCount);
    }

    [Fact]
    public async Task Tracked_Ingredient_IsMissing_When_Available_Is_Zero()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var flour = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(flour.Id, available: 0m, unit);

        var recipe = BuildRecipe(flour.Id, 500m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Missing, line.Status);
    }

    // ── Unit-gap distinction (plantry-z2sr) ──────────────────────────────────

    [Fact]
    public async Task Missing_Due_To_Unconvertible_On_Hand_Stock_Sets_UnitMismatch()
    {
        // 1 lb of onions on hand, recipe calls for "1 ea" — no weight↔count conversion. The row reads
        // Missing (can't convert to compare), but there IS stock, so the display-only UnitMismatch flag
        // is set so the UI shows "can't compare units", not "Not in your pantry" (the dogfood repro).
        var h = new Harness();
        var recipeUnit = Guid.CreateVersion7(); // "ea"
        var stockUnit = Guid.CreateVersion7();  // "lb" — IdentityUnitConverter fails across the two
        var onions = h.Catalog.AddTrackedLeaf(stockUnit, "Onions");
        h.Stock.Add(onions.Id, available: 1m, stockUnit);

        var recipe = BuildRecipe(onions.Id, 1m, recipeUnit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Missing, line.Status);
        Assert.True(line.UnitMismatch);
        // Cookability rollup is unaffected — this stays a Missing so shortfall/shopping are unchanged.
        Assert.Equal(1, result.Overall.MissingCount);
    }

    // ── Parent/variant rollup (DM-19) ─────────────────────────────────────────

    [Fact]
    public async Task Parent_Product_Rolls_Up_Stock_Across_All_Variants()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        var milk = h.Catalog.AddParent([v1, v2], "Milk");

        // v1 has 200ml, v2 has 400ml → total 600ml available (unit is the same for both).
        h.Stock.Add(v1, available: 200m, unit);
        h.Stock.Add(v2, available: 400m, unit);

        var recipe = BuildRecipe(milk.Id, 500m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        // 600ml total >= 500ml required → InStock
        Assert.Equal(IngredientStatus.InStock, line.Status);
        Assert.Equal(600m, line.AvailableQuantity);
        Assert.True(result.Overall.FullyCookable);
    }

    [Fact]
    public async Task Parent_Product_Is_Low_When_Partial_Variant_Stock_Insufficient()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        var milk = h.Catalog.AddParent([v1, v2], "Milk");

        // v1 has 100ml, v2 has 200ml → total 300ml, required 500ml → Low.
        h.Stock.Add(v1, available: 100m, unit);
        h.Stock.Add(v2, available: 200m, unit);

        var recipe = BuildRecipe(milk.Id, 500m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Low, line.Status);
        Assert.Equal(300m, line.AvailableQuantity);
    }

    [Fact]
    public async Task Parent_Product_Is_Missing_When_No_Variant_Has_Stock()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        var milk = h.Catalog.AddParent([v1, v2], "Milk");
        // No stock added for any variant.

        var recipe = BuildRecipe(milk.Id, 500m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Missing, line.Status);
    }

    // ── Expiry-soon flag (J1/J3) ──────────────────────────────────────────────

    [Fact]
    public async Task Expiring_Soon_Flag_Is_Set_When_Soonest_Expiry_Within_4_Days()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var cream = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(cream.Id, available: 500m, unit, soonestExpiry: Today.AddDays(3));

        var recipe = BuildRecipe(cream.Id, 100m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.InStock, line.Status);
        Assert.NotNull(line.ExpiresWithinDays);
        Assert.Equal(3, line.ExpiresWithinDays);
    }

    [Fact]
    public async Task Expiring_Soon_Flag_Is_Set_When_Expiry_Is_Today()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var cream = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(cream.Id, available: 500m, unit, soonestExpiry: Today);

        var recipe = BuildRecipe(cream.Id, 100m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.NotNull(line.ExpiresWithinDays);
        Assert.Equal(0, line.ExpiresWithinDays);
    }

    [Fact]
    public async Task Expiring_Soon_Flag_Is_Not_Set_When_Expiry_Beyond_4_Days()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var butter = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(butter.Id, available: 500m, unit, soonestExpiry: Today.AddDays(5));

        var recipe = BuildRecipe(butter.Id, 100m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Null(line.ExpiresWithinDays);
    }

    [Fact(DisplayName = "ExpiresWithinDays honours the configured horizon from the reader, not a fixed constant")]
    public async Task Expiring_Soon_Flag_Follows_Configured_Horizon()
    {
        // A lot 6 days out is beyond the default 4-day window but inside a configured 10-day horizon.
        // The flag must follow the household setting the reader supplies (plantry-5yhd).
        var unit = Guid.CreateVersion7();

        var wide = new Harness { HorizonDays = 10 };
        var creamWide = wide.Catalog.AddTrackedLeaf(unit);
        wide.Stock.Add(creamWide.Id, available: 500m, unit, soonestExpiry: Today.AddDays(6));
        var wideResult = await wide.Service.ComputeAsync(BuildRecipe(creamWide.Id, 100m, unit), 4, Today);
        Assert.Equal(6, Assert.Single(wideResult.Lines).ExpiresWithinDays);

        var narrow = new Harness { HorizonDays = 4 };
        var creamNarrow = narrow.Catalog.AddTrackedLeaf(unit);
        narrow.Stock.Add(creamNarrow.Id, available: 500m, unit, soonestExpiry: Today.AddDays(6));
        var narrowResult = await narrow.Service.ComputeAsync(BuildRecipe(creamNarrow.Id, 100m, unit), 4, Today);
        Assert.Null(Assert.Single(narrowResult.Lines).ExpiresWithinDays);
    }

    [Fact]
    public async Task Expiring_Soon_Flag_Is_Not_Set_When_No_Expiry_Date()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var rice = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(rice.Id, available: 1000m, unit, soonestExpiry: null);

        var recipe = BuildRecipe(rice.Id, 100m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Null(line.ExpiresWithinDays);
    }

    // ── Serving scale changes status ──────────────────────────────────────────

    [Fact]
    public async Task Scaling_Up_Servings_Can_Change_InStock_To_Low()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var eggs = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(eggs.Id, available: 4m, unit); // 4 eggs available

        // Recipe: 2 eggs for 2 servings → at 4 servings scaled required = 4 → InStock
        // At 6 servings scaled required = 6 → Low
        var recipe = BuildRecipe(eggs.Id, 2m, unit, defaultServings: 2);

        var resultAt4 = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);
        Assert.Equal(IngredientStatus.InStock, resultAt4.Lines[0].Status);

        var resultAt6 = await h.Service.ComputeAsync(recipe, desiredServings: 6, today: Today);
        Assert.Equal(IngredientStatus.Low, resultAt6.Lines[0].Status);
    }

    [Fact]
    public async Task Scaling_Down_Servings_Can_Change_Missing_To_InStock()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var butter = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(butter.Id, available: 50m, unit);

        // Recipe: 100g butter for 4 servings → at 4 servings required=100 → Low
        // At 2 servings scaled required = 50 → InStock
        var recipe = BuildRecipe(butter.Id, 100m, unit, defaultServings: 4);

        var resultAt4 = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);
        Assert.Equal(IngredientStatus.Low, resultAt4.Lines[0].Status);

        var resultAt2 = await h.Service.ComputeAsync(recipe, desiredServings: 2, today: Today);
        Assert.Equal(IngredientStatus.InStock, resultAt2.Lines[0].Status);
    }

    // ── Details page bug (plantry-6vg): fulfillment must recompute at selected servings ─────────

    /// <summary>
    /// Regression guard for plantry-6vg: the Detail page used to call FulfillmentService with
    /// DefaultServings HARDCODED, so scaling the recipe past available stock left it falsely
    /// showing "cookable". This test asserts that the service produces a different result when
    /// desiredServings exceeds the stock threshold — the handler must pass the user-selected
    /// servings, not DefaultServings.
    /// Repro: 500 g available; recipe needs 400 g at 2 servings (InStock); needs 800 g at 4 (Low).
    /// </summary>
    [Fact]
    public async Task Fulfillment_At_Selected_Servings_Reflects_Scale_Past_Available_Stock()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var chickpeas = h.Catalog.AddTrackedLeaf(unit, "Chickpeas");
        h.Stock.Add(chickpeas.Id, available: 500m, unit);

        // 400 g at default 2 servings → InStock at 2; Low when doubled to 4 (needs 800 g).
        var recipe = BuildRecipe(chickpeas.Id, 400m, unit, defaultServings: 2);

        var atDefault = await h.Service.ComputeAsync(recipe, desiredServings: 2, today: Today);
        Assert.Equal(IngredientStatus.InStock, atDefault.Lines[0].Status);
        Assert.True(atDefault.Overall.FullyCookable, "Should be cookable at default servings.");

        var atDoubled = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);
        Assert.Equal(IngredientStatus.Low, atDoubled.Lines[0].Status);
        Assert.False(atDoubled.Overall.FullyCookable, "Should NOT be cookable when scaled past available stock.");
        Assert.Equal(1, atDoubled.Overall.LowCount);
        Assert.Equal(0, atDoubled.Overall.MissingCount);
    }

    // ── Expired stock (negative ExpiresWithinDays) — plantry-17n ─────────────

    [Fact]
    public async Task Expired_Lot_Sets_Negative_ExpiresWithinDays()
    {
        // Lot expired 3 days ago → ExpiresWithinDays == -3 (not clamped to 0).
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var cream = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(cream.Id, available: 200m, unit, soonestExpiry: Today.AddDays(-3));

        var recipe = BuildRecipe(cream.Id, 100m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.NotNull(line.ExpiresWithinDays);
        Assert.Equal(-3, line.ExpiresWithinDays);
    }

    [Fact]
    public async Task Far_Overdue_Lot_Sets_Large_Negative_ExpiresWithinDays()
    {
        // Lot expired 30 days ago → -30 (gate admits all negatives, never re-clamped).
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var yogurt = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(yogurt.Id, available: 150m, unit, soonestExpiry: Today.AddDays(-30));

        var recipe = BuildRecipe(yogurt.Id, 50m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(-30, line.ExpiresWithinDays);
    }

    [Fact]
    public async Task InStock_And_Expired_Reports_InStock_With_Negative_ExpiresWithinDays()
    {
        // An ingredient can be InStock (available >= required) AND expired at the same time.
        // Status and ExpiresWithinDays are orthogonal — expiry flag does not block cookability.
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var milk = h.Catalog.AddTrackedLeaf(unit);
        h.Stock.Add(milk.Id, available: 500m, unit, soonestExpiry: Today.AddDays(-2));

        var recipe = BuildRecipe(milk.Id, 200m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.InStock, line.Status);
        Assert.Equal(-2, line.ExpiresWithinDays);
        // Cookability is not blocked by expiry.
        Assert.True(result.Overall.FullyCookable);
    }

    // ── Parent/variant expiry-soon flag ──────────────────────────────────────

    [Fact]
    public async Task Parent_Product_Expiry_Soon_Uses_Soonest_Across_Variants()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        var milk = h.Catalog.AddParent([v1, v2], "Milk");

        // v1 expires in 2 days (within 4-day window), v2 expires in 10 days (outside window).
        h.Stock.Add(v1, available: 500m, unit, soonestExpiry: Today.AddDays(2));
        h.Stock.Add(v2, available: 500m, unit, soonestExpiry: Today.AddDays(10));

        var recipe = BuildRecipe(milk.Id, 100m, unit);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.InStock, line.Status);
        Assert.Equal(2, line.ExpiresWithinDays); // soonest from v1 wins
    }

    // ── Overall summary ───────────────────────────────────────────────────────

    [Fact]
    public async Task Overall_Reports_Mixed_Missing_And_Low_When_Multiple_Ingredients()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var flour = h.Catalog.AddTrackedLeaf(unit, "Flour");
        var sugar = h.Catalog.AddTrackedLeaf(unit, "Sugar");
        var salt = h.Catalog.AddUntracked("Salt");

        h.Stock.Add(flour.Id, available: 100m, unit); // Low (need 200)
        // sugar: no stock → Missing

        var recipe = Recipe.Create(Household, "Cake", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flour.Id, 200m, unit, null, 0),
            new IngredientLine(sugar.Id, 100m, unit, null, 1),
            new IngredientLine(salt.Id, null, null, null, 2),
        ], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4, today: Today);

        Assert.False(result.Overall.FullyCookable);
        Assert.Equal(1, result.Overall.MissingCount);
        Assert.Equal(1, result.Overall.LowCount);

        Assert.Equal(IngredientStatus.Low, result.Lines[0].Status);
        Assert.Equal(IngredientStatus.Missing, result.Lines[1].Status);
        Assert.Equal(IngredientStatus.Untracked, result.Lines[2].Status);
    }
}

/// <summary>
/// L1 tests for <see cref="FulfillmentService.Compute"/> — the pure overload that accepts
/// pre-loaded catalog/stock/converter data and issues zero further round-trips (ADR-021 rule 1).
/// Verifies byte-identical figures vs the async path across the same scenario set.
/// </summary>
public sealed class FulfillmentServicePureOverloadTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly DateOnly Today = new(2026, 6, 14);

    // Identity converter — same unit → same amount; different unit → fail.
    private static Result<decimal> IdentityConverter(Guid _, decimal amount, Guid from, Guid to) =>
        from == to
            ? Result<decimal>.Success(amount)
            : Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No path."));

    private static CatalogProduct UntrackedProduct(Guid id) =>
        new(id, "Salt", TrackStock: false, DefaultUnitId: Guid.CreateVersion7(),
            ParentProductId: null, IsParent: false, VariantProductIds: []);

    private static CatalogProduct TrackedLeaf(Guid id, Guid defaultUnitId) =>
        new(id, "Flour", TrackStock: true, defaultUnitId,
            ParentProductId: null, IsParent: false, VariantProductIds: []);

    private static CatalogProduct ParentProduct(Guid id, IReadOnlyList<Guid> variantIds) =>
        new(id, "Milk", TrackStock: true, DefaultUnitId: Guid.CreateVersion7(),
            ParentProductId: null, IsParent: true, VariantProductIds: variantIds);

    private static ProductStock MakeStock(Guid productId, decimal available, Guid unitId, DateOnly? expiry = null) =>
        new(productId, available, unitId, expiry);

    private static Recipe BuildRecipe(Guid productId, decimal quantity, Guid unitId, int defaultServings = 4)
    {
        var recipe = Recipe.Create(Household, "Test", defaultServings, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, quantity, unitId, null, 0)], Clock);
        return recipe;
    }

    // ── Untracked is always Untracked ─────────────────────────────────────────

    [Fact]
    public void Pure_Untracked_Is_Always_Untracked()
    {
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = UntrackedProduct(productId) };
        var stock = new Dictionary<Guid, ProductStock>();

        var recipe = Recipe.Create(Household, "Omelette", 2, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, null, null, null, 0)], Clock);

        var svc = new FulfillmentService(null!, null!, null!, null!); // ports unused by pure overload
        var result = svc.Compute(recipe, 2, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        Assert.Equal(IngredientStatus.Untracked, Assert.Single(result.Lines).Status);
        Assert.True(result.Overall.FullyCookable);
    }

    // ── InStock / Low / Missing thresholds ───────────────────────────────────

    [Fact]
    public void Pure_InStock_When_Available_Meets_Required()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock> { [productId] = MakeStock(productId, 500m, unit) };

        var recipe = BuildRecipe(productId, 500m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        Assert.Equal(IngredientStatus.InStock, Assert.Single(result.Lines).Status);
        Assert.True(result.Overall.FullyCookable);
    }

    [Fact]
    public void Pure_Low_When_Available_Is_Between_Zero_And_Required()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock> { [productId] = MakeStock(productId, 250m, unit) };

        var recipe = BuildRecipe(productId, 500m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Low, line.Status);
        Assert.Equal(250m, line.AvailableQuantity);
        Assert.Equal(0, result.Overall.MissingCount);
        Assert.Equal(1, result.Overall.LowCount);
    }

    [Fact]
    public void Pure_Missing_When_No_Stock_Record()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock>();

        var recipe = BuildRecipe(productId, 500m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        Assert.Equal(IngredientStatus.Missing, Assert.Single(result.Lines).Status);
        Assert.Equal(1, result.Overall.MissingCount);
    }

    // ── Parent/variant rollup (DM-19) ─────────────────────────────────────────

    [Fact]
    public void Pure_Parent_Rolls_Up_Stock_Across_Variants()
    {
        var unit = Guid.CreateVersion7();
        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();

        var catalog = new Dictionary<Guid, CatalogProduct>
        {
            [parentId] = ParentProduct(parentId, [v1, v2]),
            [v1] = TrackedLeaf(v1, unit),
            [v2] = TrackedLeaf(v2, unit),
        };
        var stock = new Dictionary<Guid, ProductStock>
        {
            [v1] = MakeStock(v1, 200m, unit),
            [v2] = MakeStock(v2, 400m, unit),
        };

        var recipe = BuildRecipe(parentId, 500m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.InStock, line.Status);
        Assert.Equal(600m, line.AvailableQuantity);
    }

    // ── Expiry-soon flag (J1/J3) ──────────────────────────────────────────────

    [Fact]
    public void Pure_Expiring_Soon_Set_When_Within_4_Days()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock>
        {
            [productId] = MakeStock(productId, 500m, unit, Today.AddDays(3)),
        };

        var recipe = BuildRecipe(productId, 100m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        var line = Assert.Single(result.Lines);
        Assert.Equal(3, line.ExpiresWithinDays);
    }

    [Fact]
    public void Pure_Expiring_Soon_Not_Set_When_Beyond_4_Days()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock>
        {
            [productId] = MakeStock(productId, 500m, unit, Today.AddDays(5)),
        };

        var recipe = BuildRecipe(productId, 100m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        Assert.Null(Assert.Single(result.Lines).ExpiresWithinDays);
    }

    [Fact]
    public void Pure_Expired_Lot_Sets_Negative_ExpiresWithinDays()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock>
        {
            [productId] = MakeStock(productId, 200m, unit, Today.AddDays(-3)),
        };

        var recipe = BuildRecipe(productId, 100m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        Assert.Equal(-3, Assert.Single(result.Lines).ExpiresWithinDays);
    }

    // ── Converter failure → variant contributes 0 ────────────────────────────

    [Fact]
    public void Pure_Converter_Failure_Contributes_Zero_Not_Exception()
    {
        var ingredientUnit = Guid.CreateVersion7();
        var stockUnit = Guid.CreateVersion7(); // different from ingredient unit → converter fails
        var productId = Guid.CreateVersion7();

        var catalog = new Dictionary<Guid, CatalogProduct>
        {
            [productId] = TrackedLeaf(productId, stockUnit),
        };
        var stock = new Dictionary<Guid, ProductStock>
        {
            [productId] = MakeStock(productId, 500m, stockUnit), // stock in stockUnit
        };

        // Converter: identity only — will fail for stockUnit → ingredientUnit
        var recipe = BuildRecipe(productId, 100m, ingredientUnit); // recipe asks for ingredientUnit
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        // Converter fails → available = 0 → Missing (not an exception)
        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Missing, line.Status);
        // …but there IS real stock we simply can't convert, so the display-only UnitMismatch flag is set
        // (plantry-z2sr): the row is "can't compare units", not an empty pantry.
        Assert.True(line.UnitMismatch);
    }

    // ── Unit-gap distinction (plantry-z2sr): Missing-due-to-no-conversion vs genuine empty ────

    [Fact]
    public void Pure_Genuine_Empty_Pantry_Does_Not_Set_UnitMismatch()
    {
        // No stock record at all → Missing, but this is a genuine empty pantry, NOT a unit gap.
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock>();

        var recipe = BuildRecipe(productId, 100m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Missing, line.Status);
        Assert.False(line.UnitMismatch);
    }

    [Fact]
    public void Pure_Zero_On_Hand_Unconvertible_Does_Not_Set_UnitMismatch()
    {
        // Stock exists but is empty (0 on hand) AND its unit can't convert → still a genuine Missing, not
        // a unit gap: there is nothing to compare, so we must not claim "you have some we can't compare".
        var ingredientUnit = Guid.CreateVersion7();
        var stockUnit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, stockUnit) };
        var stock = new Dictionary<Guid, ProductStock> { [productId] = MakeStock(productId, 0m, stockUnit) };

        var recipe = BuildRecipe(productId, 100m, ingredientUnit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.Missing, line.Status);
        Assert.False(line.UnitMismatch);
    }

    [Fact]
    public void Pure_InStock_Never_Sets_UnitMismatch()
    {
        // Convertible stock that satisfies the requirement → InStock and UnitMismatch stays false.
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var catalog = new Dictionary<Guid, CatalogProduct> { [productId] = TrackedLeaf(productId, unit) };
        var stock = new Dictionary<Guid, ProductStock> { [productId] = MakeStock(productId, 500m, unit) };

        var recipe = BuildRecipe(productId, 100m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        var line = Assert.Single(result.Lines);
        Assert.Equal(IngredientStatus.InStock, line.Status);
        Assert.False(line.UnitMismatch);
    }

    // ── Missing catalog entry → Missing ──────────────────────────────────────

    [Fact]
    public void Pure_Missing_Catalog_Entry_Treated_As_Missing()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        // catalog is empty — product not found
        var catalog = new Dictionary<Guid, CatalogProduct>();
        var stock = new Dictionary<Guid, ProductStock>();

        var recipe = BuildRecipe(productId, 100m, unit);
        var svc = new FulfillmentService(null!, null!, null!, null!);
        var result = svc.Compute(recipe, 4, Today, catalog, stock, IdentityConverter, expiringSoonDays: 4);

        Assert.Equal(IngredientStatus.Missing, Assert.Single(result.Lines).Status);
    }
}
