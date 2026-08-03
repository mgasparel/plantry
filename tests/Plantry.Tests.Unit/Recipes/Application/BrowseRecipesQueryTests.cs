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
    // A FIXED instant, not SystemClock.Instance (plantry-lgbu Opus pass-1 FIX, gate 10A). The previous
    // ambient SystemClock.Instance meant this static field and the SUT's own `clock.UtcNow` read
    // (Harness passes this same Clock through by default) were two independent reads of the REAL wall
    // clock, straddling local midnight — exactly the UTC-vs-local nondeterminism this ticket exists to
    // remove from production, just reintroduced in the fixture. Pinning it here makes every "use soon"
    // fixture below fully deterministic, and — as a bonus — a regression to ambient DateTime.UtcNow in
    // BrowseRecipesQuery.cs now fails 4 tests (not just the dedicated Today_* regression tests),
    // because every fixture built off Today stops matching what the SUT actually computes.
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2031, 6, 15, 12, 0, 0, TimeSpan.Zero));
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid HouseholdGuid = Household.Value;
    // Track the same clock BrowseRecipesQuery uses for its "expiring soon" comparison
    // (BrowseRecipesQuery.cs: today = clock.ToLocalDate(clock.UtcNow), plantry-l639) via the
    // IClock.Zone seam rather than the machine's real TimeZoneInfo.Local — Clock's Zone (unset,
    // defaults to UTC per IClock's DIM) must match what BrowseRecipesQuery actually reads, or every
    // fixture built off Today below would drift from the SUT on a non-UTC machine.
    private static readonly DateOnly Today = Clock.ToLocalDate(Clock.UtcNow);

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
        public readonly FakeRecipeRatingRepository Ratings = new();
        public readonly FakeHouseholdMemberReader Members = new();
        public readonly FakeInventoryStockReader Stock = new();
        public readonly FakePriceReader Prices = new();
        public readonly FakeCatalogProductReader Catalog = new();
        public readonly IdentityUnitConverter Converter = new();
        public readonly BrowseRecipesQuery Query;

        /// <param name="queryClock">The clock <see cref="BrowseRecipesQuery"/> itself reads for "today"
        /// (distinct from the fixture's own <see cref="Clock"/>, which stamps domain object timestamps).
        /// Defaults to <see cref="Clock"/> so existing fixtures are unaffected.</param>
        public Harness(IClock? queryClock = null)
        {
            var tenant = new FakeTenantContext(HouseholdGuid);
            var expansionSvc = new RecipeExpansionService(Recipes);
            var fulfillmentSvc = new FulfillmentService(Stock, Catalog, Converter, new FakeExpiringSoonHorizonReader());
            var costingSvc = new CostingService(Prices, Converter, Catalog);
            Query = new BrowseRecipesQuery(Recipes, Tags, Ratings, Members, expansionSvc, fulfillmentSvc, costingSvc, tenant, queryClock ?? Clock);
        }

        /// <summary>
        /// Adds a parent recipe that includes <paramref name="sub"/> (and has no direct ingredients),
        /// so its Browse figures are entirely driven by the expanded sub — proves inclusion-sourced badges.
        /// </summary>
        public Recipe AddParentIncluding(string name, Recipe sub, decimal servings, int defaultServings = 2)
        {
            var recipe = Recipe.Create(Household, name, defaultServings, Clock).Value;
            var result = RecipeLineSet.Create([], [new InclusionLine(sub.Id, servings, null, 0)], recipe.Id);
            Assert.True(result.IsSuccess, $"AddParentIncluding failed: {result.Error.Code}");
            recipe.ReplaceLines(result.Value, Clock);
            Recipes.Items.Add(recipe);
            return recipe;
        }

        /// <summary>
        /// Adds a recipe with one direct tracked ingredient AND an inclusion of a sub-recipe id that is NOT
        /// in the repository — modelling a tampered/dangling inclusion that bypassed the picker (N5 rules this
        /// out for legitimate recipes). Expansion against the non-archived map misses the sub, so the Browse
        /// row must degrade to FLAT computation over the direct ingredient rather than failing the page.
        /// </summary>
        public Recipe AddRecipeWithDanglingInclusion(string name, Guid productId, Guid unitId, int defaultServings = 2)
        {
            var recipe = Recipe.Create(Household, name, defaultServings, Clock).Value;
            var result = RecipeLineSet.Create(
                [new IngredientLine(productId, 100m, unitId, null, 0)],
                [new InclusionLine(RecipeId.New(), 2m, null, 1)],
                recipe.Id);
            Assert.True(result.IsSuccess, $"AddRecipeWithDanglingInclusion failed: {result.Error.Code}");
            recipe.ReplaceLines(result.Value, Clock);
            Recipes.Items.Add(recipe);
            return recipe;
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

        public void AddRating(RecipeId recipeId, Guid userId, int stars) =>
            Ratings.Items.Add(RecipeRating.Create(Household, recipeId, userId, stars, Clock));
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

    // ── Inclusion-aware badges (recipe-composition.md §7, D4 — plantry-ckzc) ─────

    [Fact]
    public async Task Row_Reflects_Expanded_Figures_When_Ingredients_Live_In_An_Inclusion()
    {
        // A parent with NO direct ingredients that includes a sub whose single tracked ingredient is Missing
        // and priced. Expanded → the parent's badges reflect the sub's cheese: Missing, priced.
        // A (buggy) FLAT computation over the parent's own (empty) ingredient list would instead show
        // TotalIngredientCount 0, MissingCount 0, FullyCookable true, CostCompleteness None — so these
        // assertions fail unless expansion is actually driving the row.
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var cheese = h.Catalog.AddTrackedLeaf(unit, "Cheese");
        // No stock for cheese → Missing.
        h.Prices.Add(cheese.Id, 0.01m, unit); // $0.01/unit

        // Sub: DefaultServings 2, one tracked ingredient of 100 cheese.
        var sub = h.AddRecipe("Cheese Sauce", defaultServings: 2, productId: cheese.Id, unitId: unit);
        // Parent: DefaultServings 2, includes 2 servings of the sub → factor = 2/2 = 1, no direct ingredients.
        h.AddParentIncluding("Nachos", sub, servings: 2m, defaultServings: 2);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(NameQuery: "Nachos"));

        var row = Assert.Single(result.Rows);
        Assert.Equal("Nachos", row.Name);
        Assert.Equal(1, row.TotalIngredientCount);      // the expanded cheese line (not the 0 direct ingredients)
        Assert.False(row.FullyCookable);
        Assert.Equal(1, row.MissingCount);
        Assert.Equal(0, row.FulfillmentPct);
        // Cost is the expanded, scaled cheese cost per serving: (100 × $0.01) / 2 servings = $0.50.
        Assert.Equal(CostCompleteness.Full, row.CostCompleteness);
        Assert.Equal(0.5m, row.CostPerServing);
    }

    [Fact]
    public async Task Dangling_Inclusion_Degrades_That_Row_To_Flat_Without_Failing_The_Page()
    {
        // A recipe with a direct tracked (Missing) ingredient AND an inclusion whose sub id is absent from the
        // non-archived set (a tampered/dangling inclusion). Expansion fails for THIS row; it must degrade to
        // flat computation over the direct ingredient (Edge 1) — the page renders, the row is present.
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var butter = h.Catalog.AddTrackedLeaf(unit, "Butter");
        // No stock → Missing.

        h.AddRecipe("Healthy", defaultServings: 2, productId: butter.Id, unitId: unit); // a normal sibling row
        h.AddRecipeWithDanglingInclusion("Tampered", butter.Id, unit, defaultServings: 2);

        // The whole query must succeed (no throw) and return BOTH rows.
        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        Assert.Equal(2, result.Rows.Count);
        var tampered = Assert.Single(result.Rows, r => r.Name == "Tampered");
        // Flat fallback computed over the single direct ingredient (the dangling inclusion contributes nothing).
        Assert.Equal(1, tampered.TotalIngredientCount);
        Assert.Equal(1, tampered.MissingCount);
        Assert.False(tampered.FullyCookable);
    }

    // ── "today" regression pins (plantry-lgbu AC5, rewritten hermetically by plantry-l639) ──────
    //
    // BrowseRecipesQuery.cs previously read `DateOnly.FromDateTime(DateTime.UtcNow)` — the real
    // ambient wall clock, ignoring dependency injection entirely — instead of the household's
    // server-local "today" via the injected IClock. Two distinct failure modes, two distinct tests:
    //   1. The clock is read from IClock at all, not the real ambient wall clock.
    //   2. The date is the LOCAL calendar day of that instant (via IClock.Zone), not its UTC
    //      calendar day.
    // (1) is proven by pinning the fixture's clock far from the real "now" (any regression to
    // ambient DateTime.UtcNow then computes a wildly different date, regardless of when/where the
    // test runs). (2) is proven below by two FixedClock instances carrying an explicit, non-UTC
    // TimeZoneInfo (west and east of UTC) — the fixture's own zone constructs the local-vs-UTC day
    // split directly, so both cases run deterministically on every machine (no environment-dependent
    // guard, no CI timezone pin, per plantry-l639's IClock.Zone seam). A regression to
    // TimeZoneInfo.Local (or to DateTimeOffset.LocalDateTime, which reads it implicitly) ignores the
    // fixture's zone entirely and — on a UTC-zoned CI/dev machine — collapses local and UTC day back
    // together, flipping the "expiring soon" flag asserted below.

    [Fact]
    public async Task Today_Is_Read_From_The_Injected_Clock_Not_The_Real_Ambient_Wall_Clock()
    {
        // Fixed far in the future — nowhere near whatever the real wall-clock date happens to be
        // when this test executes, on any machine, at any time.
        var fixedClock = new FixedClock(new DateTimeOffset(2031, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var fixedLocalToday = fixedClock.ToLocalDate(fixedClock.UtcNow);

        var h = new Harness(fixedClock);
        var unit = Guid.CreateVersion7();
        var product = h.Catalog.AddTrackedLeaf(unit, "Milk");
        // Expires exactly on the fixed clock's local "today" — inside the 7-day horizon only if
        // "today" is actually read from the fixed clock. The real ambient clock's actual "today" is
        // over a thousand days away from this date, so a regression to ambient DateTime.UtcNow
        // computes a "today" nowhere near this expiry and the flag would not fire.
        h.Stock.Add(product.Id, 500m, unit, fixedLocalToday);
        h.AddRecipe("Milk toast", productId: product.Id, unitId: unit);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        Assert.True(result.Rows[0].HasIngredientExpiringSoon);
    }

    [Fact]
    public async Task Today_Uses_The_Local_Calendar_Day_West_Of_Utc_Not_The_Utc_Calendar_Day_Of_The_Same_Instant()
    {
        // A fixed -05:00 zone (never the real machine zone) — 23:00 local on the 15th is already
        // 04:00 UTC on the 16th, so the UTC calendar day has rolled over to "tomorrow" while the
        // local calendar day is still "today". This is exactly BrowseRecipesQuery.cs's documented
        // west-of-UTC failure mode, constructed deterministically instead of depending on the
        // machine's real offset.
        var westZone = TimeZoneInfo.CreateCustomTimeZone("Fixed-05:00", TimeSpan.FromHours(-5), "Fixed -05:00", "Fixed -05:00");
        var localAnchor = new DateTime(2031, 6, 15, 23, 0, 0, DateTimeKind.Unspecified);
        var utcInstant = new DateTimeOffset(localAnchor, TimeSpan.FromHours(-5));
        var localDay = DateOnly.FromDateTime(localAnchor);

        // Sanity: this instant really does read as "tomorrow" in UTC.
        Assert.Equal(localDay.AddDays(1), DateOnly.FromDateTime(utcInstant.UtcDateTime));

        var fixedClock = new FixedClock(utcInstant, westZone);
        var h = new Harness(fixedClock);
        var unit = Guid.CreateVersion7();
        var product = h.Catalog.AddTrackedLeaf(unit, "Milk");
        // One day past the 7-day horizon measured from the LOCAL day (8 > 7 → not flagged). Measured
        // from the UTC calendar day of this same instant it would be exactly 7 days out (7 <= 7 →
        // wrongly flagged) — the flip that would expose a TimeZoneInfo.Local/LocalDateTime regression
        // that ignores the fixture's -05:00 zone.
        h.Stock.Add(product.Id, 500m, unit, localDay.AddDays(8));
        h.AddRecipe("Milk toast", productId: product.Id, unitId: unit);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        Assert.False(result.Rows[0].HasIngredientExpiringSoon);
    }

    [Fact]
    public async Task Today_Uses_The_Local_Calendar_Day_East_Of_Utc_Not_The_Utc_Calendar_Day_Of_The_Same_Instant()
    {
        // The symmetric east-of-UTC case: a fixed +13:00 zone where noon local on the 16th is still
        // 23:00 UTC on the 15th — the local calendar day has already rolled over to "tomorrow" while
        // the UTC calendar day is still "today".
        var eastZone = TimeZoneInfo.CreateCustomTimeZone("Fixed+13:00", TimeSpan.FromHours(13), "Fixed +13:00", "Fixed +13:00");
        var localAnchor = new DateTime(2031, 6, 16, 12, 0, 0, DateTimeKind.Unspecified);
        var utcInstant = new DateTimeOffset(localAnchor, TimeSpan.FromHours(13));
        var localDay = DateOnly.FromDateTime(localAnchor);

        // Sanity: this instant really does read as "yesterday" in UTC.
        Assert.Equal(localDay.AddDays(-1), DateOnly.FromDateTime(utcInstant.UtcDateTime));

        var fixedClock = new FixedClock(utcInstant, eastZone);
        var h = new Harness(fixedClock);
        var unit = Guid.CreateVersion7();
        var product = h.Catalog.AddTrackedLeaf(unit, "Milk");
        // Exactly on the 7-day horizon measured from the LOCAL day (7 <= 7 → flagged). Measured from
        // the UTC calendar day of this same instant it would be 8 days out (8 > 7 → wrongly NOT
        // flagged) — the flip that would expose a TimeZoneInfo.Local/LocalDateTime regression that
        // ignores the fixture's +13:00 zone.
        h.Stock.Add(product.Id, 500m, unit, localDay.AddDays(7));
        h.AddRecipe("Milk toast", productId: product.Id, unitId: unit);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter());

        Assert.True(result.Rows[0].HasIngredientExpiringSoon);
    }

    // ── Ratings (plantry-zlwp.1) ─────────────────────────────────────────────

    [Fact]
    public async Task Unrated_Recipe_Has_Null_MyStars_And_HouseholdAvg_And_Zero_RatedCount()
    {
        var h = new Harness();
        h.AddRecipe("Pasta");

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(), userId: Guid.NewGuid());

        var row = Assert.Single(result.Rows);
        Assert.Null(row.MyStars);
        Assert.Null(row.HouseholdAvg);
        Assert.Equal(0, row.RatedCount);
    }

    [Fact]
    public async Task MyStars_Reflects_The_Calling_Users_Own_Rating()
    {
        var h = new Harness();
        var recipe = h.AddRecipe("Pasta");
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        h.AddRating(recipe.Id, me, 4);
        h.AddRating(recipe.Id, other, 2);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(), userId: me);

        var row = Assert.Single(result.Rows);
        Assert.Equal(4, row.MyStars);
    }

    [Fact]
    public async Task Null_UserId_Yields_Null_MyStars_But_Household_Aggregate_Still_Computed()
    {
        var h = new Harness();
        var recipe = h.AddRecipe("Pasta");
        h.AddRating(recipe.Id, Guid.NewGuid(), 4);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(), userId: null);

        var row = Assert.Single(result.Rows);
        Assert.Null(row.MyStars);
        Assert.Equal(4.0m, row.HouseholdAvg);
        Assert.Equal(1, row.RatedCount);
    }

    [Fact]
    public async Task HouseholdAvg_Is_The_One_Decimal_Average_Across_All_Raters()
    {
        var h = new Harness();
        var recipe = h.AddRecipe("Pasta");
        h.AddRating(recipe.Id, Guid.NewGuid(), 4);
        h.AddRating(recipe.Id, Guid.NewGuid(), 5);
        h.AddRating(recipe.Id, Guid.NewGuid(), 5);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(), userId: Guid.NewGuid());

        var row = Assert.Single(result.Rows);
        // (4 + 5 + 5) / 3 = 4.666... -> rounds to 4.7 (1dp).
        Assert.Equal(4.7m, row.HouseholdAvg);
        Assert.Equal(3, row.RatedCount);
    }

    [Fact]
    public async Task Ratings_On_One_Recipe_Never_Leak_Onto_Another_Recipes_Row()
    {
        var h = new Harness();
        var rated = h.AddRecipe("Rated");
        var unrated = h.AddRecipe("Unrated");
        h.AddRating(rated.Id, Guid.NewGuid(), 5);

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(), userId: Guid.NewGuid());

        var ratedRow = result.Rows.Single(r => r.RecipeId == rated.Id.Value);
        var unratedRow = result.Rows.Single(r => r.RecipeId == unrated.Id.Value);
        Assert.Equal(1, ratedRow.RatedCount);
        Assert.Equal(0, unratedRow.RatedCount);
        Assert.Null(unratedRow.HouseholdAvg);
    }

    // ── Rating breakdown + sort (plantry-zlwp.4) ──────────────────────────────

    [Fact]
    public async Task Breakdown_Is_The_Union_Of_Household_Members_And_Raters_With_Caller_First()
    {
        var h = new Harness();
        var recipe = h.AddRecipe("Pasta");
        var me = Guid.NewGuid();
        var alex = Guid.NewGuid();
        var sam = Guid.NewGuid();
        h.Members.Items.Add(new HouseholdMember(me, "Michael", "M"));
        h.Members.Items.Add(new HouseholdMember(alex, "Alex", "A"));
        h.Members.Items.Add(new HouseholdMember(sam, "Sam", "S"));
        h.AddRating(recipe.Id, me, 4);
        h.AddRating(recipe.Id, alex, 5);
        // Sam has not rated — still appears in the breakdown with Stars = null.

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(), userId: me);

        var row = Assert.Single(result.Rows);
        Assert.Equal(3, row.Breakdown!.Count);
        Assert.Equal("Michael", row.Breakdown[0].DisplayName);
        Assert.True(row.Breakdown[0].IsCurrentUser);
        Assert.Equal(4, row.Breakdown[0].Stars);
        var samRow = row.Breakdown.Single(m => m.DisplayName == "Sam");
        Assert.Null(samRow.Stars);
    }

    [Fact]
    public async Task Breakdown_Is_Empty_When_Nobody_Has_Rated_And_No_Directory_Entries()
    {
        var h = new Harness();
        h.AddRecipe("Pasta");

        var result = await h.Query.ExecuteAsync(new BrowseRecipesFilter(), userId: Guid.NewGuid());

        var row = Assert.Single(result.Rows);
        Assert.Empty(row.Breakdown!);
    }

    [Fact]
    public async Task Sort_By_Rating_Descending_Highest_Average_First_Unrated_Last()
    {
        var h = new Harness();
        var low = h.AddRecipe("Low");
        var high = h.AddRecipe("High");
        h.AddRecipe("Unrated");
        h.AddRating(low.Id, Guid.NewGuid(), 3);
        h.AddRating(high.Id, Guid.NewGuid(), 5);

        var filter = new BrowseRecipesFilter(Sort: BrowseSort.Rating, SortDescending: true);
        var result = await h.Query.ExecuteAsync(filter, userId: Guid.NewGuid());

        Assert.Equal("High",    result.Rows[0].Name);
        Assert.Equal("Low",     result.Rows[1].Name);
        Assert.Equal("Unrated", result.Rows[2].Name);
    }

    [Fact]
    public async Task Sort_By_Rating_Ascending_Lowest_Average_First_Unrated_STILL_Last()
    {
        // The ticket's "nulls last" rule holds regardless of direction — ascending must NOT
        // put the unrated recipe first just because null naively sorts low.
        var h = new Harness();
        var low = h.AddRecipe("Low");
        var high = h.AddRecipe("High");
        h.AddRecipe("Unrated");
        h.AddRating(low.Id, Guid.NewGuid(), 3);
        h.AddRating(high.Id, Guid.NewGuid(), 5);

        var filter = new BrowseRecipesFilter(Sort: BrowseSort.Rating, SortDescending: false);
        var result = await h.Query.ExecuteAsync(filter, userId: Guid.NewGuid());

        Assert.Equal("Low",     result.Rows[0].Name);
        Assert.Equal("High",    result.Rows[1].Name);
        Assert.Equal("Unrated", result.Rows[2].Name);
    }
}
