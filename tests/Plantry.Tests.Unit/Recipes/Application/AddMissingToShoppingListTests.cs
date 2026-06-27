using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using InventoryProductStock = Plantry.Recipes.Application.ProductStock;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 tests for <see cref="AddMissingToShoppingList"/> (P2-4a, recipes-domain-model.md §7, J5).
/// Uses faked <see cref="IShoppingListWriter"/> and <see cref="IInventoryStockReader"/> (plus the
/// other FulfillmentService ports from the shared test-doubles file).
///
/// Covers:
/// <list type="bullet">
///   <item>Missing AND Low lines are forwarded (not InStock, not Untracked).</item>
///   <item>Quantities emitted are the shortfall (scaledRequired − available), not the full quantity.</item>
///   <item>Untracked staples (null Quantity/UnitId) are excluded even when cataloged as tracked
///     (defensive guard — FulfillmentService classifies them as Untracked, not Missing).</item>
///   <item>Quantities are scaled to the displayed serving count.</item>
///   <item>source="recipe" and source_ref=recipeId are passed correctly.</item>
///   <item>NothingMissing result when all ingredients are in stock.</item>
///   <item>NotFound result when the recipe does not exist.</item>
///   <item>Unauthorized result when there is no tenant context.</item>
///   <item>Invalid result when desiredServings &lt; 1.</item>
/// </list>
/// </summary>
public sealed class AddMissingToShoppingListTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdGuid = Guid.CreateVersion7();

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeShoppingListWriter : IShoppingListWriter
    {
        public List<(IReadOnlyList<ShoppingItem> Items, string Source, Guid SourceRef)> Calls { get; } = [];

        public Task AddItemsAsync(
            IEnumerable<ShoppingItem> items,
            string source,
            Guid sourceRef,
            CancellationToken ct = default)
        {
            Calls.Add((items.ToList(), source, sourceRef));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInventoryStockReader : IInventoryStockReader
    {
        private readonly Dictionary<Guid, InventoryProductStock> _stock = [];

        public void Add(Guid productId, decimal available, Guid defaultUnitId) =>
            _stock[productId] = new InventoryProductStock(productId, available, defaultUnitId, null);

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

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required FakeRecipeRepository Recipes { get; init; }
        public required FakeCatalogProductReader Catalog { get; init; }
        public required FakeInventoryStockReader Stock { get; init; }
        public required FakeShoppingListWriter Writer { get; init; }
        public required AddMissingToShoppingList Service { get; init; }
    }

    private Harness BuildHarness(bool authenticated = true)
    {
        var recipes = new FakeRecipeRepository();
        var catalog = new FakeCatalogProductReader();
        var stock = new FakeInventoryStockReader();
        var writer = new FakeShoppingListWriter();
        var tenant = new FakeTenantContext(authenticated ? _householdGuid : (Guid?)null);
        var fulfillment = new FulfillmentService(stock, catalog, new IdentityUnitConverter());
        var service = new AddMissingToShoppingList(recipes, fulfillment, writer, Clock, tenant);
        return new Harness
        {
            Recipes = recipes,
            Catalog = catalog,
            Stock = stock,
            Writer = writer,
            Service = service,
        };
    }

    private HouseholdId Household => HouseholdId.From(_householdGuid);

    // ── Guard: no tenant context ──────────────────────────────────────────────

    [Fact(DisplayName = "Returns Unauthorized when no household context")]
    public async Task Returns_Unauthorized_When_No_Tenant()
    {
        var h = BuildHarness(authenticated: false);
        var result = await h.Service.ExecuteAsync(RecipeId.New(), desiredServings: 2);
        Assert.IsType<AddMissingResult.Unauthorized>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── Guard: invalid servings ───────────────────────────────────────────────

    [Fact(DisplayName = "Returns Invalid when desiredServings < 1")]
    public async Task Returns_Invalid_When_Servings_Less_Than_One()
    {
        var h = BuildHarness();
        var result = await h.Service.ExecuteAsync(RecipeId.New(), desiredServings: 0);
        Assert.IsType<AddMissingResult.Invalid>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── Guard: recipe not found ───────────────────────────────────────────────

    [Fact(DisplayName = "Returns NotFound when recipe does not exist")]
    public async Task Returns_NotFound_When_Recipe_Does_Not_Exist()
    {
        var h = BuildHarness();
        var result = await h.Service.ExecuteAsync(RecipeId.New(), desiredServings: 2);
        Assert.IsType<AddMissingResult.NotFound>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── NothingMissing when all in stock ──────────────────────────────────────

    [Fact(DisplayName = "Returns NothingMissing when all ingredients are InStock")]
    public async Task Returns_NothingMissing_When_All_InStock()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var product = h.Catalog.AddTracked(unitId);
        h.Stock.Add(product.Id, available: 500m, unitId); // plenty in stock

        var recipe = BuildRecipe(Household, defaultServings: 4, product.Id, qty: 100m, unitId);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, desiredServings: 4);

        Assert.IsType<AddMissingResult.NothingMissing>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── Missing AND Low lines forwarded; InStock and Untracked are excluded ───

    [Fact(DisplayName = "Missing AND Low lines are forwarded; InStock and Untracked are excluded")]
    public async Task Missing_And_Low_Lines_Are_Forwarded()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();

        // InStock product: 500g available, needs 100g — not forwarded
        var inStockProduct = h.Catalog.AddTracked(unitId, "InStock Product");
        h.Stock.Add(inStockProduct.Id, 500m, unitId);

        // Low product: 50g available, needs 100g → shortfall = 50g
        var lowProduct = h.Catalog.AddTracked(unitId, "Low Product");
        h.Stock.Add(lowProduct.Id, 50m, unitId);

        // Missing product: 0 available → shortfall = 100g (full scaled quantity)
        var missingProduct = h.Catalog.AddTracked(unitId, "Missing Product");
        // (no stock added → Missing)

        var recipe = Recipe.Create(Household, "Test Recipe", 4, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(inStockProduct.Id,  100m, unitId, null, 0),
            new IngredientLine(lowProduct.Id,       100m, unitId, null, 1),
            new IngredientLine(missingProduct.Id,   100m, unitId, null, 2),
        ], Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, desiredServings: 4);

        var added = Assert.IsType<AddMissingResult.Added>(result);
        Assert.Equal(2, added.ItemCount); // Low + Missing both forwarded

        var call = Assert.Single(h.Writer.Calls);
        Assert.Equal(2, call.Items.Count);

        // Low line: shortfall = 100 - 50 = 50
        var lowItem = call.Items.Single(i => i.ProductId == lowProduct.Id);
        Assert.Equal(50m, lowItem.Quantity);
        Assert.Equal(unitId, lowItem.UnitId);

        // Missing line: shortfall = 100 - 0 = 100 (full required)
        var missingItem = call.Items.Single(i => i.ProductId == missingProduct.Id);
        Assert.Equal(100m, missingItem.Quantity);
        Assert.Equal(unitId, missingItem.UnitId);
    }

    // ── Untracked staples excluded (C12) ─────────────────────────────────────

    [Fact(DisplayName = "Untracked staples (null qty/unit) are never forwarded to shopping (C12)")]
    public async Task Untracked_Staples_Are_Excluded()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();

        // An untracked staple: track_stock=false, no Quantity/UnitId
        var stapleId = Guid.CreateVersion7();
        h.Catalog.Register(new CatalogProduct(stapleId, "Salt", TrackStock: false,
            DefaultUnitId: Guid.CreateVersion7(), ParentProductId: null, IsParent: false, VariantProductIds: []));

        // Missing tracked product
        var missingProduct = h.Catalog.AddTracked(unitId, "Flour");

        var recipe = Recipe.Create(Household, "Bread", 4, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(stapleId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0),
            new IngredientLine(missingProduct.Id, 200m, unitId, null, 1),
        ], Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, desiredServings: 4);

        var added = Assert.IsType<AddMissingResult.Added>(result);
        Assert.Equal(1, added.ItemCount);

        var call = Assert.Single(h.Writer.Calls);
        var item = Assert.Single(call.Items);
        // Only the tracked missing product, not the staple.
        Assert.Equal(missingProduct.Id, item.ProductId);
    }

    // ── Quantity scaling ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Quantities are scaled by desiredServings / defaultServings")]
    public async Task Quantities_Are_Scaled()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var product = h.Catalog.AddTracked(unitId);
        // No stock → Missing

        var recipe = Recipe.Create(Household, "Pasta", defaultServings: 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(product.Id, 200m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        // Ask for 6 servings when default is 4 → scale = 6/4 = 1.5 → 200 * 1.5 = 300
        var result = await h.Service.ExecuteAsync(recipe.Id, desiredServings: 6);

        var added = Assert.IsType<AddMissingResult.Added>(result);
        Assert.Equal(1, added.ItemCount);

        var call = Assert.Single(h.Writer.Calls);
        var item = Assert.Single(call.Items);
        Assert.Equal(300m, item.Quantity);
    }

    // ── Provenance (source / source_ref) ─────────────────────────────────────

    [Fact(DisplayName = "source='recipe' and source_ref=recipeId are stamped on the AddItems call")]
    public async Task Source_And_SourceRef_Are_Correct()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var product = h.Catalog.AddTracked(unitId);
        // No stock → Missing

        var recipe = Recipe.Create(Household, "Risotto", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(product.Id, 100m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        await h.Service.ExecuteAsync(recipe.Id, desiredServings: 2);

        var call = Assert.Single(h.Writer.Calls);
        Assert.Equal(AddMissingToShoppingList.RecipeSource, call.Source);
        Assert.Equal(recipe.Id.Value, call.SourceRef);
    }

    // ── Multiple missing items ────────────────────────────────────────────────

    [Fact(DisplayName = "All missing items are forwarded in a single AddItems call")]
    public async Task Multiple_Missing_Items_Forwarded_In_One_Call()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var product1 = h.Catalog.AddTracked(unitId, "Onion");
        var product2 = h.Catalog.AddTracked(unitId, "Garlic");
        // Neither has stock → both Missing

        var recipe = Recipe.Create(Household, "Sauce", 2, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(product1.Id, 2m, unitId, null, 0),
            new IngredientLine(product2.Id, 3m, unitId, null, 1),
        ], Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, desiredServings: 2);

        var added = Assert.IsType<AddMissingResult.Added>(result);
        Assert.Equal(2, added.ItemCount);

        var call = Assert.Single(h.Writer.Calls); // single batch call
        Assert.Equal(2, call.Items.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Recipe BuildRecipe(
        HouseholdId household,
        int defaultServings,
        Guid productId,
        decimal qty,
        Guid unitId)
    {
        var r = Recipe.Create(household, "Test Recipe", defaultServings, SystemClock.Instance).Value;
        r.ReplaceIngredients(
            [new IngredientLine(productId, qty, unitId, null, 0)],
            SystemClock.Instance);
        return r;
    }

    /// <summary>Identity converter (same unit = same amount).</summary>
    private sealed class IdentityUnitConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            Task.FromResult(fromUnitId == toUnitId
                ? Result<decimal>.Success(amount)
                : Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No path.")));
    }
}
