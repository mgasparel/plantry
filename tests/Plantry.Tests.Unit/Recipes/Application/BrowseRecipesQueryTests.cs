using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 tests for <see cref="BrowseRecipesQuery"/> — all dependencies faked in-memory.
/// Covers: filter combinations (name search, tag filter, Use-soon, AND-combine), sort dimensions
/// (Fulfillment/Cost computed after reads; Name/CookTime/RecentlyAdded from local index), and the
/// rule that fully-cookable count counts all recipes (not just filtered ones).
/// </summary>
public sealed class BrowseRecipesQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid HouseholdGuid = Household.Value;
    private static readonly DateOnly Today = new(2026, 6, 14);

    // ── Dedicated test doubles for BrowseRecipesQuery ────────────────────────

    private sealed class FakeInventoryStockReader : IInventoryStockReader
    {
        private readonly Dictionary<Guid, ProductStock> _stock = [];

        public void Add(Guid productId, decimal available, Guid defaultUnitId, DateOnly? soonestExpiry = null) =>
            _stock[productId] = new ProductStock(productId, available, defaultUnitId, soonestExpiry);

        public Task<ProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_stock.GetValueOrDefault(productId));

        public Task<IReadOnlyDictionary<Guid, ProductStock>> FindStockBatchAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, ProductStock> result = productIds
                .Where(_stock.ContainsKey)
                .ToDictionary(id => id, id => _stock[id]);
            return Task.FromResult(result);
        }
    }

    private sealed class FakePriceReader : IPriceReader
    {
        private readonly Dictionary<Guid, PricePoint> _prices = [];

        public void Add(Guid productId, decimal unitPrice, Guid unitId) =>
            _prices[productId] = new PricePoint(productId, unitPrice, 1m, unitId, unitPrice);

        public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_prices.GetValueOrDefault(productId));
    }

    private sealed class FakeCatalogProductReader : ICatalogProductReader
    {
        private readonly Dictionary<Guid, CatalogProduct> _products = [];

        public CatalogProduct AddUntracked(Guid unitId, string name = "Salt") =>
            Register(new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: false, unitId, null, false, []));

        public CatalogProduct AddTrackedLeaf(Guid unitId, string name = "Flour") =>
            Register(new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: true, unitId, null, false, []));

        private CatalogProduct Register(CatalogProduct p) { _products[p.Id] = p; return p; }

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

    /// <summary>Identity converter — same unit converts to itself. Sufficient for all tests here.</summary>
    private sealed class IdentityUnitConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            Task.FromResult(fromUnitId == toUnitId
                ? Result<decimal>.Success(amount)
                : Result<decimal>.Failure(Error.Custom("Test.NoPath", "No unit path.")));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly FakeRecipeRepository Recipes = new();
        public readonly FakeTagRepository Tags = new();
        public readonly FakeInventoryStockReader Stock = new();
        public readonly FakePriceReader Prices = new();
        public readonly FakeCatalogProductReader Catalog = new();
        public readonly IdentityUnitConverter Converter = new();
        public readonly BrowseRecipesQuery Query;

        public Harness()
        {
            var tenant = new FakeTenantContext(HouseholdGuid);
            var fulfillmentSvc = new FulfillmentService(Stock, Catalog, Converter);
            var costingSvc = new CostingService(Prices, Converter);
            Query = new BrowseRecipesQuery(Recipes, Tags, fulfillmentSvc, costingSvc, tenant);
        }

        /// <summary>
        /// Adds a recipe with one untracked staple ingredient (always Untracked / fully satisfiable).
        /// Use <paramref name="productId"/> and <paramref name="unitId"/> to wire real tracked stock.
        /// </summary>
        public Recipe AddRecipe(
            string name,
            int defaultServings = 4,
            int? cookTime = null,
            IReadOnlyList<TagId>? tags = null,
            Guid? productId = null,
            Guid? unitId = null)
        {
            var recipe = Recipe.Create(Household, name, defaultServings, Clock).Value;
            if (cookTime.HasValue) recipe.SetCookTime(cookTime.Value, Clock);
            if (tags?.Count > 0) recipe.SetTags(tags, Clock);

            if (productId.HasValue && unitId.HasValue)
            {
                // Tracked ingredient — caller controls stock / catalog entries.
                recipe.ReplaceIngredients(
                    [new IngredientLine(productId.Value, 100m, unitId.Value, null, 0)], Clock);
            }
            else
            {
                // Untracked staple — always satisfiable (Untracked → cookable).
                var uid = Guid.CreateVersion7();
                var staple = Catalog.AddUntracked(uid, $"Staple-{name}");
                recipe.ReplaceIngredients(
                    [new IngredientLine(staple.Id, null, null, null, 0)], Clock);
            }

            Recipes.Items.Add(recipe);
            return recipe;
        }

        public Tag AddTag(string name)
        {
            var tag = Tag.Create(Household, name, null, Clock);
            Tags.Items.Add(tag);
            return tag;
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_Recipes_Returns_Empty_Rows()
    {
        var h = new Harness();
        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.CookableCount);
    }

    [Fact]
    public async Task All_Tags_Included_In_Result_Regardless_Of_Active_Filter()
    {
        var h = new Harness();
        var vegTag = h.AddTag("Vegetarian");
        var meatTag = h.AddTag("Meat");
        h.AddRecipe("Salad", tags: [vegTag.Id]);

        // Filter by meatTag: zero rows, but AllTags still lists all household tags.
        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(TagId: meatTag.Id.Value));

        Assert.Empty(result.Rows);
        Assert.Equal(2, result.AllTags.Count);
    }

    [Fact]
    public async Task Name_Filter_Is_Case_Insensitive_Contains()
    {
        var h = new Harness();
        h.AddRecipe("Tomato Pasta");
        h.AddRecipe("Chicken Soup");

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(NameQuery: "tomato"));

        Assert.Single(result.Rows);
        Assert.Equal("Tomato Pasta", result.Rows[0].Name);
    }

    [Fact]
    public async Task Tag_Filter_Returns_Only_Recipes_With_That_Tag()
    {
        var h = new Harness();
        var vegTag = h.AddTag("Vegetarian");
        h.AddRecipe("Salad", tags: [vegTag.Id]);
        h.AddRecipe("Steak");

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(TagId: vegTag.Id.Value));

        Assert.Single(result.Rows);
        Assert.Equal("Salad", result.Rows[0].Name);
    }

    [Fact]
    public async Task UseSoon_Filter_Returns_Only_Recipes_With_Expiring_Ingredient()
    {
        // Set up two recipes: one with an ingredient expiring in 2 days, one without.
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var expProduct = h.Catalog.AddTrackedLeaf(unit, "Soon-expiring milk");
        h.Stock.Add(expProduct.Id, 500m, unit, Today.AddDays(2)); // expires soon (≤4 days)

        var freshProduct = h.Catalog.AddTrackedLeaf(unit, "Fresh milk");
        h.Stock.Add(freshProduct.Id, 500m, unit, Today.AddDays(30)); // not soon

        h.AddRecipe("Soon pasta",  productId: expProduct.Id,   unitId: unit);
        h.AddRecipe("Fresh pasta", productId: freshProduct.Id, unitId: unit);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(UseSoon: true));

        Assert.Single(result.Rows);
        Assert.Equal("Soon pasta", result.Rows[0].Name);
        Assert.True(result.Rows[0].HasIngredientExpiringSoon);
    }

    [Fact]
    public async Task Filters_Are_And_Combined()
    {
        // Name AND tag AND UseSoon must all match.
        var h = new Harness();
        var vegTag = h.AddTag("Vegetarian");
        var unit = Guid.CreateVersion7();

        var expProduct = h.Catalog.AddTrackedLeaf(unit, "Milk");
        h.Stock.Add(expProduct.Id, 500m, unit, Today.AddDays(1)); // expiring soon

        var freshProduct = h.Catalog.AddTrackedLeaf(unit, "Cream");
        h.Stock.Add(freshProduct.Id, 500m, unit, Today.AddDays(30));

        // Matches name+tag+soon: Veggie Pasta (has veg tag, "pasta" in name, expiring soon)
        h.AddRecipe("Veggie Pasta", tags: [vegTag.Id], productId: expProduct.Id, unitId: unit);
        // Missing tag: Meat Pasta (has "pasta" + soon, no veg tag)
        h.AddRecipe("Meat Pasta",   productId: expProduct.Id, unitId: unit);
        // Missing soon: Veggie Rice (has veg tag + "rice" — but wait, q="pasta" so filtered)
        h.AddRecipe("Veggie Rice",  tags: [vegTag.Id], productId: freshProduct.Id, unitId: unit);

        var filter = new BrowseRecipesFilter(
            NameQuery: "pasta",
            TagId: vegTag.Id.Value,
            UseSoon: true);

        var result = await h.Query.ExecuteAsync(filter);

        Assert.Single(result.Rows);
        Assert.Equal("Veggie Pasta", result.Rows[0].Name);
    }

    [Fact]
    public async Task Default_Sort_Is_Fulfillment_Descending()
    {
        // Cookable recipe (all untracked → 100%) ranks above a recipe with a missing ingredient.
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var missingProduct = h.Catalog.AddTrackedLeaf(unit, "Butter");
        // No stock for missingProduct → Missing status → low fulfillment.

        h.AddRecipe("High cookable");   // untracked → 100%
        h.AddRecipe("Low missing", productId: missingProduct.Id, unitId: unit); // 0%

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        Assert.Equal("High cookable", result.Rows[0].Name);
        Assert.Equal("Low missing",   result.Rows[1].Name);
    }

    [Fact]
    public async Task Sort_By_Fulfillment_Ascending_Reverses_Order()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var missingProduct = h.Catalog.AddTrackedLeaf(unit, "Eggs");

        h.AddRecipe("High");  // 100%
        h.AddRecipe("Low", productId: missingProduct.Id, unitId: unit); // 0%

        var filter = new BrowseRecipesFilter(Sort: BrowseSort.Fulfillment, SortDescending: false);
        var result = await h.Query.ExecuteAsync(filter);

        Assert.Equal("Low",  result.Rows[0].Name);
        Assert.Equal("High", result.Rows[1].Name);
    }

    [Fact]
    public async Task Sort_By_Name_Ascending_Is_Alphabetical()
    {
        var h = new Harness();
        h.AddRecipe("Zucchini");
        h.AddRecipe("Apple");
        h.AddRecipe("Mango");

        var filter = new BrowseRecipesFilter(Sort: BrowseSort.Name, SortDescending: false);
        var result = await h.Query.ExecuteAsync(filter);

        Assert.Equal("Apple",    result.Rows[0].Name);
        Assert.Equal("Mango",    result.Rows[1].Name);
        Assert.Equal("Zucchini", result.Rows[2].Name);
    }

    [Fact]
    public async Task Sort_By_CookTime_Ascending_Quickest_First()
    {
        var h = new Harness();
        h.AddRecipe("Slow",    cookTime: 90);
        h.AddRecipe("Quick",   cookTime: 15);
        h.AddRecipe("NoTime"); // null → last

        var filter = new BrowseRecipesFilter(Sort: BrowseSort.CookTime, SortDescending: false);
        var result = await h.Query.ExecuteAsync(filter);

        Assert.Equal("Quick",   result.Rows[0].Name);
        Assert.Equal("Slow",    result.Rows[1].Name);
        Assert.Equal("NoTime",  result.Rows[2].Name);
    }

    [Fact]
    public async Task Sort_By_Cost_Ascending_Cheapest_First()
    {
        // Wire real prices so CostingService computes non-null amounts.
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var cheapProduct = h.Catalog.AddTrackedLeaf(unit, "Rice");
        h.Stock.Add(cheapProduct.Id, 500m, unit);
        h.Prices.Add(cheapProduct.Id, 0.01m, unit); // $0.01/unit → $1/serving (100 * 0.01)

        var expProduct = h.Catalog.AddTrackedLeaf(unit, "Truffle");
        h.Stock.Add(expProduct.Id, 500m, unit);
        h.Prices.Add(expProduct.Id, 0.10m, unit); // $0.10/unit → $10/serving

        h.AddRecipe("Cheap",     productId: cheapProduct.Id, unitId: unit);
        h.AddRecipe("Expensive", productId: expProduct.Id,   unitId: unit);
        h.AddRecipe("NoCost");   // untracked ingredient → CostCompleteness.None

        var filter = new BrowseRecipesFilter(Sort: BrowseSort.Cost, SortDescending: false);
        var result = await h.Query.ExecuteAsync(filter);

        // Priced recipes come first (NoCost = MaxValue placeholder for sort).
        Assert.Equal("Cheap",     result.Rows[0].Name);
        Assert.Equal("Expensive", result.Rows[1].Name);
        Assert.Equal("NoCost",    result.Rows[2].Name);
    }

    [Fact]
    public async Task CookableCount_Counts_All_Recipes_Not_Just_Filtered_Set()
    {
        var h = new Harness();
        var vegTag = h.AddTag("Vegetarian");

        // Both are cookable (untracked ingredient), only one has the vegTag.
        h.AddRecipe("Veggie", tags: [vegTag.Id]);
        h.AddRecipe("Meat");

        var filter = new BrowseRecipesFilter(TagId: vegTag.Id.Value);
        var result = await h.Query.ExecuteAsync(filter);

        Assert.Single(result.Rows);        // filter narrows to one
        Assert.Equal(2, result.CookableCount); // both recipes are cookable
    }

    [Fact]
    public async Task Cost_Is_Null_When_No_Price_Data()
    {
        var h = new Harness();
        // Tracked ingredient, in-stock, but no price → CostCompleteness.None.
        var unit = Guid.CreateVersion7();
        var product = h.Catalog.AddTrackedLeaf(unit, "Flour");
        h.Stock.Add(product.Id, 500m, unit);
        // No price registered for this product.

        h.AddRecipe("NoPriceRecipe", productId: product.Id, unitId: unit);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        var row = Assert.Single(result.Rows);
        Assert.Null(row.CostPerServing);
        Assert.Equal(CostCompleteness.None, row.CostCompleteness);
    }

    [Fact]
    public async Task Fulfillment_Missing_Ingredient_Gives_Low_Pct()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var product = h.Catalog.AddTrackedLeaf(unit, "Butter");
        // No stock → Missing status → pct = 0%

        h.AddRecipe("No butter", productId: product.Id, unitId: unit);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        var row = Assert.Single(result.Rows);
        Assert.Equal(0, row.FulfillmentPct);
        Assert.False(row.FullyCookable);
        Assert.Equal(1, row.MissingCount);
    }

    [Fact]
    public async Task Use_Soon_Badge_When_Ingredient_Expires_Within_4_Days()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var product = h.Catalog.AddTrackedLeaf(unit, "Milk");
        h.Stock.Add(product.Id, 500m, unit, soonestExpiry: Today.AddDays(3)); // within 4 days

        h.AddRecipe("Milk recipe", productId: product.Id, unitId: unit);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        Assert.True(result.Rows[0].HasIngredientExpiringSoon);
    }
}
