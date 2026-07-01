using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 tests for <see cref="AddIngredientsToShoppingList"/> (plantry-s1z).
/// Uses faked <see cref="IShoppingListWriter"/> and <see cref="IRecipeRepository"/>.
///
/// Covers:
/// <list type="bullet">
///   <item>ALL tracked (quantity-bearing) ingredients are forwarded — not just missing/low.</item>
///   <item>Untracked staples (null Quantity/UnitId) are excluded.</item>
///   <item>Quantity-bearing ingredients whose product is untracked (track_stock=false) or unknown
///     to the household are excluded — the add-set matches the "tracked" UI label (plantry-yukq).</item>
///   <item>Quantities are scaled to the desired serving count.</item>
///   <item>source="recipe" and source_ref=recipeId are stamped correctly.</item>
///   <item>NothingToAdd result when all ingredients are untracked staples.</item>
///   <item>NotFound result when the recipe does not exist.</item>
///   <item>Unauthorized result when there is no tenant context.</item>
///   <item>Invalid result when servings &lt; 1.</item>
/// </list>
/// </summary>
public sealed class AddIngredientsToShoppingListTests
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

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required FakeRecipeRepository Recipes { get; init; }
        public required FakeShoppingListWriter Writer { get; init; }
        public required FakeCatalogProductReader Products { get; init; }
        public required AddIngredientsToShoppingList Service { get; init; }
    }

    private Harness BuildHarness(bool authenticated = true)
    {
        var recipes = new FakeRecipeRepository();
        var writer = new FakeShoppingListWriter();
        var products = new FakeCatalogProductReader();
        var tenant = new FakeTenantContext(authenticated ? _householdGuid : (Guid?)null);
        var service = new AddIngredientsToShoppingList(recipes, writer, products, tenant);
        return new Harness
        {
            Recipes = recipes,
            Writer = writer,
            Products = products,
            Service = service,
        };
    }

    private HouseholdId Household => HouseholdId.From(_householdGuid);

    // ── Guard: no tenant context ──────────────────────────────────────────────

    [Fact(DisplayName = "Returns Unauthorized when no household context")]
    public async Task Returns_Unauthorized_When_No_Tenant()
    {
        var h = BuildHarness(authenticated: false);
        var result = await h.Service.ExecuteAsync(RecipeId.New(), servings: 2);
        Assert.IsType<AddIngredientsResult.Unauthorized>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── Guard: invalid servings ───────────────────────────────────────────────

    [Fact(DisplayName = "Returns Invalid when servings < 1")]
    public async Task Returns_Invalid_When_Servings_Less_Than_One()
    {
        var h = BuildHarness();
        var result = await h.Service.ExecuteAsync(RecipeId.New(), servings: 0);
        Assert.IsType<AddIngredientsResult.Invalid>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── Guard: recipe not found ───────────────────────────────────────────────

    [Fact(DisplayName = "Returns NotFound when recipe does not exist")]
    public async Task Returns_NotFound_When_Recipe_Does_Not_Exist()
    {
        var h = BuildHarness();
        var result = await h.Service.ExecuteAsync(RecipeId.New(), servings: 2);
        Assert.IsType<AddIngredientsResult.NotFound>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── NothingToAdd when all ingredients are untracked staples ──────────────

    [Fact(DisplayName = "Returns NothingToAdd when all ingredients are untracked staples (null qty/unit)")]
    public async Task Returns_NothingToAdd_When_All_Staples()
    {
        var h = BuildHarness();
        var stapleId = Guid.CreateVersion7();

        var recipe = Recipe.Create(Household, "Staple Only Recipe", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(stapleId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, servings: 2);

        Assert.IsType<AddIngredientsResult.NothingToAdd>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── ALL tracked ingredients forwarded (not just missing/low) ─────────────

    [Fact(DisplayName = "All quantity-bearing ingredients are forwarded regardless of stock level")]
    public async Task All_Tracked_Ingredients_Forwarded()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var product1 = Guid.CreateVersion7();
        var product2 = Guid.CreateVersion7();
        var product3 = Guid.CreateVersion7();

        h.Products.RegisterTracked(product1);
        h.Products.RegisterTracked(product2);
        h.Products.RegisterTracked(product3);

        var recipe = Recipe.Create(Household, "Full Recipe", 4, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(product1, 100m, unitId, null, 0), // would be InStock
            new IngredientLine(product2, 200m, unitId, null, 1), // would be Low
            new IngredientLine(product3, 150m, unitId, null, 2), // would be Missing
        ], Clock);
        h.Recipes.Items.Add(recipe);

        // No inventory consulted — all three are forwarded unconditionally.
        var result = await h.Service.ExecuteAsync(recipe.Id, servings: 4);

        var added = Assert.IsType<AddIngredientsResult.Added>(result);
        Assert.Equal(3, added.ItemCount);

        var call = Assert.Single(h.Writer.Calls);
        Assert.Equal(3, call.Items.Count);
        Assert.Contains(call.Items, i => i.ProductId == product1 && i.Quantity == 100m);
        Assert.Contains(call.Items, i => i.ProductId == product2 && i.Quantity == 200m);
        Assert.Contains(call.Items, i => i.ProductId == product3 && i.Quantity == 150m);
    }

    // ── Untracked staples excluded ────────────────────────────────────────────

    [Fact(DisplayName = "Untracked staples (null Quantity/UnitId) are excluded; tracked ingredients are forwarded")]
    public async Task Untracked_Staples_Are_Excluded()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var trackedId = Guid.CreateVersion7();
        var stapleId = Guid.CreateVersion7();

        h.Products.RegisterTracked(trackedId);

        var recipe = Recipe.Create(Household, "Mixed Recipe", 2, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(trackedId, 300m, unitId, null, 0),
            new IngredientLine(stapleId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 1),
        ], Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, servings: 2);

        var added = Assert.IsType<AddIngredientsResult.Added>(result);
        Assert.Equal(1, added.ItemCount);

        var call = Assert.Single(h.Writer.Calls);
        var item = Assert.Single(call.Items);
        Assert.Equal(trackedId, item.ProductId);
        Assert.Equal(300m, item.Quantity);
        Assert.Equal(unitId, item.UnitId);
    }

    // ── Quantity-bearing untracked staple excluded (track_stock = false) ─────

    [Fact(DisplayName = "Quantity-bearing ingredient with an untracked product (track_stock=false) is excluded from the add-set")]
    public async Task QuantityBearing_Untracked_Product_Is_Excluded()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var trackedId = Guid.CreateVersion7();
        var untrackedStapleId = Guid.CreateVersion7();

        // Both ingredients carry a Quantity+UnitId, but the staple's product is track_stock=false
        // in Catalog. The service must exclude it so the add-set matches the _DetailsFulfilmentCard
        // "tracked" label, which counts only non-Untracked fulfilment lines (plantry-yukq).
        h.Products.RegisterTracked(trackedId);
        h.Products.RegisterUntracked(untrackedStapleId);

        var recipe = Recipe.Create(Household, "Salted Recipe", 2, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(trackedId, 300m, unitId, null, 0),
            new IngredientLine(untrackedStapleId, 5m, unitId, null, 1), // untracked staple w/ incidental qty
        ], Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, servings: 2);

        var added = Assert.IsType<AddIngredientsResult.Added>(result);
        Assert.Equal(1, added.ItemCount);

        var call = Assert.Single(h.Writer.Calls);
        var item = Assert.Single(call.Items);
        Assert.Equal(trackedId, item.ProductId);
        Assert.DoesNotContain(call.Items, i => i.ProductId == untrackedStapleId);
    }

    // ── Quantity-bearing ingredient absent from Catalog is excluded ───────────

    [Fact(DisplayName = "Quantity-bearing ingredient whose product is unknown to the household is excluded")]
    public async Task QuantityBearing_Unknown_Product_Is_Excluded()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var trackedId = Guid.CreateVersion7();
        var unknownId = Guid.CreateVersion7(); // never registered in the catalog reader

        h.Products.RegisterTracked(trackedId);

        var recipe = Recipe.Create(Household, "Orphan Recipe", 2, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(trackedId, 300m, unitId, null, 0),
            new IngredientLine(unknownId, 5m, unitId, null, 1),
        ], Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, servings: 2);

        var added = Assert.IsType<AddIngredientsResult.Added>(result);
        Assert.Equal(1, added.ItemCount);

        var call = Assert.Single(h.Writer.Calls);
        var item = Assert.Single(call.Items);
        Assert.Equal(trackedId, item.ProductId);
    }

    // ── NothingToAdd when the only quantity-bearing ingredient is untracked ───

    [Fact(DisplayName = "Returns NothingToAdd when every quantity-bearing ingredient is untracked")]
    public async Task Returns_NothingToAdd_When_All_QuantityBearing_Untracked()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var untrackedId = Guid.CreateVersion7();

        h.Products.RegisterUntracked(untrackedId);

        var recipe = Recipe.Create(Household, "All Untracked", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(untrackedId, 5m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, servings: 2);

        Assert.IsType<AddIngredientsResult.NothingToAdd>(result);
        Assert.Empty(h.Writer.Calls);
    }

    // ── Quantity scaling ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Quantities are scaled by servings / defaultServings")]
    public async Task Quantities_Are_Scaled()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        h.Products.RegisterTracked(productId);

        // Default 4 servings, 200g per serving block → ask for 6 → scale = 1.5 → 300g
        var recipe = Recipe.Create(Household, "Pasta", defaultServings: 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, 200m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        await h.Service.ExecuteAsync(recipe.Id, servings: 6);

        var call = Assert.Single(h.Writer.Calls);
        var item = Assert.Single(call.Items);
        Assert.Equal(300m, item.Quantity); // 200 * (6/4) = 300
    }

    // ── Provenance (source / source_ref) ─────────────────────────────────────

    [Fact(DisplayName = "source='recipe' and source_ref=recipeId are stamped on the AddItems call")]
    public async Task Source_And_SourceRef_Are_Correct()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        h.Products.RegisterTracked(productId);

        var recipe = Recipe.Create(Household, "Risotto", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, 100m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        await h.Service.ExecuteAsync(recipe.Id, servings: 2);

        var call = Assert.Single(h.Writer.Calls);
        Assert.Equal(AddIngredientsToShoppingList.RecipeSource, call.Source);
        Assert.Equal(recipe.Id.Value, call.SourceRef);
    }

    // ── Multiple items dispatched in a single call ────────────────────────────

    [Fact(DisplayName = "All ingredients are dispatched in a single AddItems call")]
    public async Task All_Items_Dispatched_In_One_Call()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var p1 = Guid.CreateVersion7();
        var p2 = Guid.CreateVersion7();

        h.Products.RegisterTracked(p1);
        h.Products.RegisterTracked(p2);

        var recipe = Recipe.Create(Household, "Sauce", 2, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(p1, 2m, unitId, null, 0),
            new IngredientLine(p2, 3m, unitId, null, 1),
        ], Clock);
        h.Recipes.Items.Add(recipe);

        var result = await h.Service.ExecuteAsync(recipe.Id, servings: 2);

        var added = Assert.IsType<AddIngredientsResult.Added>(result);
        Assert.Equal(2, added.ItemCount);

        // Exactly one batch call (not one-per-ingredient).
        var call = Assert.Single(h.Writer.Calls);
        Assert.Equal(2, call.Items.Count);
    }
}
