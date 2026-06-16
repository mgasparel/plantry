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

        public FulfillmentService Service => new(Stock, Catalog, Converter);
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
