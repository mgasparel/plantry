using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

/// <summary>
/// L1 tests for <see cref="CostingService"/> — all dependencies faked in-memory.
/// Covers: Full (all costable ingredients priced), Partial (some priced → under-estimate +
/// MissingPriceProductIds), None (nothing priced → null Amount), and untracked staples
/// excluded from costable count (recipes-domain-model.md §6/7).
/// </summary>
public sealed class CostingServiceTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakePriceReader : IPriceReader
    {
        private readonly Dictionary<Guid, PricePoint> _prices = [];
        private readonly Dictionary<Guid, int> _callCounts = [];

        /// <summary>Registers a price: <paramref name="price"/> for <paramref name="quantity"/> of <paramref name="unitId"/>.</summary>
        public void Add(Guid productId, decimal price, decimal quantity, Guid unitId, decimal? unitPrice = null) =>
            _prices[productId] = new PricePoint(productId, price, quantity, unitId, unitPrice);

        /// <summary>Number of times <see cref="FindLatestAsync"/> was called for the given product id — used to
        /// assert memoization (a ref shared across lines is fetched at most once per compute call).</summary>
        public int CallCount(Guid productId) => _callCounts.GetValueOrDefault(productId);

        public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
        {
            _callCounts[productId] = _callCounts.GetValueOrDefault(productId) + 1;
            return Task.FromResult(_prices.GetValueOrDefault(productId));
        }
    }

    /// <summary>Identity converter — passes when units are the same; fails otherwise (explicit paths can be added).</summary>
    private sealed class FakeUnitConverter : IUnitConverter
    {
        private readonly Dictionary<(Guid Product, Guid From, Guid To), decimal> _paths = [];

        public void AddPath(Guid productId, Guid from, Guid to, decimal factor) =>
            _paths[(productId, from, to)] = factor;

        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
        {
            if (fromUnitId == toUnitId)
                return Task.FromResult(Result<decimal>.Success(amount));
            if (_paths.TryGetValue((productId, fromUnitId, toUnitId), out var factor))
                return Task.FromResult(Result<decimal>.Success(amount * factor));
            return Task.FromResult(Result<decimal>.Failure(
                Error.Custom("Catalog.NoConversionPath", "No conversion path.")));
        }
    }

    /// <summary>
    /// Minimal catalog reader test double — registers products (leaf or parent/variant, DM-19) and
    /// counts <see cref="FindManyWithVariantsAsync"/> calls so the async-path batching test (exactly
    /// one catalog round-trip per compute) can assert on it.
    /// </summary>
    private sealed class FakeCatalogProductReader : ICatalogProductReader
    {
        private readonly Dictionary<Guid, CatalogProduct> _products = [];

        public int FindManyWithVariantsCallCount { get; private set; }

        public void RegisterLeaf(Guid id, Guid defaultUnitId, string name = "Product") =>
            _products[id] = new CatalogProduct(id, name, TrackStock: true, defaultUnitId, null, false, []);

        public void RegisterParent(Guid id, Guid defaultUnitId, IReadOnlyList<Guid> variantIds, string name = "Parent") =>
            _products[id] = new CatalogProduct(id, name, TrackStock: true, defaultUnitId, null, true, variantIds);

        public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_products.GetValueOrDefault(productId));

        public Task<IReadOnlyDictionary<Guid, CatalogProduct>> FindManyWithVariantsAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        {
            FindManyWithVariantsCallCount++;
            IReadOnlyDictionary<Guid, CatalogProduct> result = productIds
                .Distinct()
                .Where(_products.ContainsKey)
                .ToDictionary(id => id, id => _products[id]);
            return Task.FromResult(result);
        }

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

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public FakePriceReader Prices { get; } = new();
        public FakeUnitConverter Converter { get; } = new();
        public FakeCatalogProductReader Catalog { get; } = new();
        public CostingService Service => new(Prices, Converter, Catalog);
    }

    /// <summary>
    /// Builds a recipe with a single tracked ingredient at the given product/qty/unit.
    /// </summary>
    private static Recipe BuildSingleIngredientRecipe(Guid productId, decimal quantity, Guid unitId, int defaultServings = 4)
    {
        var recipe = Recipe.Create(Household, "Test Recipe", defaultServings, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, quantity, unitId, null, 0)], Clock);
        return recipe;
    }

    // ── Full completeness (all costable ingredients priced) ───────────────────

    [Fact]
    public async Task Full_When_All_Costable_Ingredients_Are_Priced()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        // $2.00 for 500g → $0.004/g
        h.Prices.Add(productId, price: 2.00m, quantity: 500m, unitId: unit);

        // Recipe: 250g for 4 servings, compute at 4 → scaled = 250g
        // cost = 250 × $0.004 = $1.00, per serving = $0.25
        var recipe = BuildSingleIngredientRecipe(productId, 250m, unit, defaultServings: 4);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.NotNull(result.Amount);
        Assert.Equal(0.25m, result.Amount.Value, precision: 6);
        Assert.Equal(1, result.PricedCount);
        Assert.Equal(1, result.CostableCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Full_Amount_Scales_With_DesiredServings()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        // $1.00 for 100g → $0.01/g
        h.Prices.Add(productId, price: 1.00m, quantity: 100m, unitId: unit);

        // Recipe: 100g for 2 default servings
        // At 4 servings: scaled = 200g, cost = $2.00, per serving = $0.50
        var recipe = BuildSingleIngredientRecipe(productId, 100m, unit, defaultServings: 2);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.50m, result.Amount!.Value, precision: 6);
    }

    [Fact]
    public async Task Full_Uses_UnitPrice_When_Available()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        // UnitPrice already computed: $0.005/g (overrides deriving from Price/Quantity)
        h.Prices.Add(productId, price: 999m, quantity: 1m, unitId: unit, unitPrice: 0.005m);

        // Recipe: 200g for 4 servings (identity unit → no conversion needed)
        // cost = 200 × $0.005 = $1.00, per serving = $0.25
        var recipe = BuildSingleIngredientRecipe(productId, 200m, unit, defaultServings: 4);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.25m, result.Amount!.Value, precision: 6);
    }

    [Fact]
    public async Task Full_With_Multiple_Priced_Ingredients()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var flourId = Guid.CreateVersion7();
        var sugarId = Guid.CreateVersion7();

        // $1.00 for 1000g flour → $0.001/g; $2.00 for 500g sugar → $0.004/g
        h.Prices.Add(flourId, price: 1.00m, quantity: 1000m, unitId: unit);
        h.Prices.Add(sugarId, price: 2.00m, quantity: 500m, unitId: unit);

        var recipe = Recipe.Create(Household, "Cake", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),   // 200g flour @ $0.001/g = $0.20
            new IngredientLine(sugarId, 100m, unit, null, 1),   // 100g sugar @ $0.004/g = $0.40
        ], Clock);

        // Total = $0.60, per serving (4) = $0.15
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.15m, result.Amount!.Value, precision: 6);
        Assert.Equal(2, result.PricedCount);
        Assert.Equal(2, result.CostableCount);
    }

    // ── Partial completeness (some priced → under-estimate + MissingPriceProductIds) ────────────

    [Fact]
    public async Task Partial_When_Some_Ingredients_Have_No_Price()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var flourId = Guid.CreateVersion7();
        var sugarId = Guid.CreateVersion7();   // no price registered

        // $1.00 for 1000g flour → $0.001/g
        h.Prices.Add(flourId, price: 1.00m, quantity: 1000m, unitId: unit);

        var recipe = Recipe.Create(Household, "Cake", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),
            new IngredientLine(sugarId, 100m, unit, null, 1),
        ], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
        Assert.NotNull(result.Amount);  // under-estimate from flour only
        Assert.Equal(1, result.PricedCount);
        Assert.Equal(2, result.CostableCount);
        Assert.Single(result.MissingPriceProductIds);
        Assert.Contains(sugarId, result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Partial_Amount_Is_Under_Estimate_Of_Only_Priced_Ingredients()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var flourId = Guid.CreateVersion7();
        var sugarId = Guid.CreateVersion7();

        // $2.00 for 200g → $0.01/g
        h.Prices.Add(flourId, price: 2.00m, quantity: 200m, unitId: unit);
        // sugarId has no price → missing

        var recipe = Recipe.Create(Household, "Mix", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),
            new IngredientLine(sugarId, 100m, unit, null, 1),
        ], Clock);

        // Flour cost at 4 servings = 200g × $0.01 = $2.00 / 4 = $0.50 per serving (under-estimate)
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
        Assert.Equal(0.50m, result.Amount!.Value, precision: 6);
    }

    // ── None completeness (nothing priced → null Amount) ─────────────────────

    [Fact]
    public async Task None_When_No_Costable_Ingredient_Has_A_Price()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var flourId = Guid.CreateVersion7();
        var sugarId = Guid.CreateVersion7();
        // No prices registered.

        var recipe = Recipe.Create(Household, "Mystery Cake", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),
            new IngredientLine(sugarId, 100m, unit, null, 1),
        ], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Null(result.Amount);   // never shown as zero (J3)
        Assert.Equal(0, result.PricedCount);
        Assert.Equal(2, result.CostableCount);
    }

    // ── Untracked staples excluded from costable count ────────────────────────

    [Fact]
    public async Task Untracked_Staple_Excluded_From_CostableCount()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var flourId = Guid.CreateVersion7();
        var saltId = Guid.CreateVersion7();   // untracked staple: null qty/unit

        h.Prices.Add(flourId, price: 1.00m, quantity: 1000m, unitId: unit);

        var recipe = Recipe.Create(Household, "Bread", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),
            // Untracked staple — null Quantity and UnitId (R5)
            new IngredientLine(saltId, null, null, null, 1),
        ], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        // Salt excluded → costable=1, priced=1 → Full
        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(1, result.CostableCount);
        Assert.Equal(1, result.PricedCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Untracked_Staple_Does_Not_Appear_In_MissingPriceProductIds()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var saltId = Guid.CreateVersion7();   // untracked — null qty/unit, no price registered

        var recipe = Recipe.Create(Household, "Salted Water", 1, Clock).Value;
        // Only ingredient is untracked staple
        recipe.ReplaceIngredients([new IngredientLine(saltId, null, null, null, 0)], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 1);

        // CostableCount = 0 (no costable ingredients) → None, but salt must NOT appear in MissingPriceProductIds
        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Null(result.Amount);
        Assert.Equal(0, result.CostableCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Mixed_Tracked_And_Untracked_Reports_Correct_Counts()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();

        var flourId = Guid.CreateVersion7();
        var sugarId = Guid.CreateVersion7();
        var saltId = Guid.CreateVersion7();    // untracked

        h.Prices.Add(flourId, price: 1.00m, quantity: 1000m, unitId: unit);
        // sugarId has no price; saltId is untracked (excluded)

        var recipe = Recipe.Create(Household, "Sweet Bread", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),
            new IngredientLine(sugarId, 100m, unit, null, 1),
            new IngredientLine(saltId, null, null, null, 2),
        ], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
        Assert.Equal(2, result.CostableCount);   // flour + sugar only (salt excluded)
        Assert.Equal(1, result.PricedCount);     // only flour
        Assert.Single(result.MissingPriceProductIds);
        Assert.Contains(sugarId, result.MissingPriceProductIds);
        Assert.DoesNotContain(saltId, result.MissingPriceProductIds);
    }

    // ── None when recipe has only untracked staples ───────────────────────────

    [Fact]
    public async Task None_When_All_Ingredients_Are_Untracked_Staples()
    {
        var h = new Harness();

        var saltId = Guid.CreateVersion7();
        var pepperSpiritsId = Guid.CreateVersion7();

        var recipe = Recipe.Create(Household, "Season", 1, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(saltId, null, null, null, 0),
            new IngredientLine(pepperSpiritsId, null, null, null, 1),
        ], Clock);

        var result = await h.Service.ComputeAsync(recipe, desiredServings: 1);

        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Null(result.Amount);
        Assert.Equal(0, result.CostableCount);
        Assert.Equal(0, result.PricedCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    // ── DM-19: parent → variant price rollup (plantry-daal) ───────────────────

    [Fact]
    public async Task Parent_With_One_Priced_Variant_Prices_The_Line()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, unit, [variantId]);
        h.Catalog.RegisterLeaf(variantId, unit);
        // $2.00 for 500 units → $0.004/unit
        h.Prices.Add(variantId, price: 2.00m, quantity: 500m, unitId: unit);

        // Recipe references the PARENT, not the variant.
        var recipe = BuildSingleIngredientRecipe(parentId, 250m, unit, defaultServings: 4);
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.25m, result.Amount!.Value, precision: 6);
        Assert.Equal(1, result.PricedCount);
        Assert.Equal(1, result.CostableCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Parent_Multiple_Priced_Variants_Same_Unit_Cheapest_Wins()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var cheapVariant = Guid.CreateVersion7();
        var pricierVariant = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, unit, [pricierVariant, cheapVariant]);
        h.Catalog.RegisterLeaf(cheapVariant, unit);
        h.Catalog.RegisterLeaf(pricierVariant, unit);
        h.Prices.Add(cheapVariant, price: 1.00m, quantity: 500m, unitId: unit);   // $0.002/unit
        h.Prices.Add(pricierVariant, price: 2.00m, quantity: 500m, unitId: unit); // $0.004/unit

        var recipe = BuildSingleIngredientRecipe(parentId, 250m, unit, defaultServings: 4);
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        // Cheapest wins: 250 × $0.002 = $0.50 / 4 servings = $0.125/serving.
        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.125m, result.Amount!.Value, precision: 6);
    }

    [Fact]
    public async Task Parent_Variants_Priced_In_Different_Units_Cheapest_Converted_Line_Cost_Wins()
    {
        var h = new Harness();
        var gram = Guid.CreateVersion7();
        var kg = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var gramVariant = Guid.CreateVersion7();
        var kgVariant = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, gram, [gramVariant, kgVariant]);
        h.Catalog.RegisterLeaf(gramVariant, gram);
        h.Catalog.RegisterLeaf(kgVariant, kg);
        // gramVariant: $0.01/g. kgVariant: $5.00/kg = $0.005/g — cheaper once converted.
        h.Prices.Add(gramVariant, price: 1.00m, quantity: 100m, unitId: gram);
        h.Prices.Add(kgVariant, price: 5.00m, quantity: 1m, unitId: kg);
        // 1 kg = 1000 g: converting 1 kg-unit → g-line-unit needs the kg→g factor; the line unit is
        // grams, so converting 1 unit of kg (the price unit) into g yields 1000.
        h.Converter.AddPath(kgVariant, kg, gram, 1000m);

        // Recipe line is expressed in grams (the parent's default unit).
        var recipe = BuildSingleIngredientRecipe(parentId, 100m, gram, defaultServings: 1);
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 1);

        // kgVariant: unitPrice $5.00/kg-unit; convert 1 kg → 1000 g ⇒ $5.00/1000 = $0.005/g ⇒ cheaper.
        // gramVariant: $0.01/g. Cheapest converted line cost = 100g × $0.005/g = $0.50.
        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.50m, result.Amount!.Value, precision: 6);
    }

    [Fact]
    public async Task Parent_One_Variant_Unconvertible_Other_Variant_Still_Prices_The_Line()
    {
        var h = new Harness();
        var lineUnit = Guid.CreateVersion7();
        var otherUnit = Guid.CreateVersion7(); // no conversion path registered to lineUnit
        var parentId = Guid.CreateVersion7();
        var unconvertibleVariant = Guid.CreateVersion7();
        var usableVariant = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, lineUnit, [unconvertibleVariant, usableVariant]);
        h.Catalog.RegisterLeaf(unconvertibleVariant, otherUnit);
        h.Catalog.RegisterLeaf(usableVariant, lineUnit);
        h.Prices.Add(unconvertibleVariant, price: 1.00m, quantity: 100m, unitId: otherUnit); // no path → skipped
        h.Prices.Add(usableVariant, price: 3.00m, quantity: 100m, unitId: lineUnit);         // $0.03/unit

        var recipe = BuildSingleIngredientRecipe(parentId, 100m, lineUnit, defaultServings: 1);
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 1);

        // The unconvertible variant is skipped as a candidate; the usable variant still prices the line.
        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(3.00m, result.Amount!.Value, precision: 6); // 100 × $0.03
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Parent_No_Variant_Priced_Line_Unpriced_Parent_Own_Id_In_MissingPriceProductIds()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, unit, [variantId]);
        h.Catalog.RegisterLeaf(variantId, unit);
        // No price registered for the variant.

        var recipe = BuildSingleIngredientRecipe(parentId, 100m, unit, defaultServings: 1);
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 1);

        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Null(result.Amount);
        Assert.Single(result.MissingPriceProductIds);
        // The PARENT's own id is in the list — never the variant's — since the UI resolves this
        // list against ingredient product ids (D2).
        Assert.Contains(parentId, result.MissingPriceProductIds);
        Assert.DoesNotContain(variantId, result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Parent_With_Zero_Live_Variants_Line_Unpriced_Parent_Id_In_Missing()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, unit, []); // no live variants

        var recipe = BuildSingleIngredientRecipe(parentId, 100m, unit, defaultServings: 1);
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 1);

        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Null(result.Amount);
        Assert.Contains(parentId, result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Parent_Partially_Priced_Variant_Set_Still_Counts_The_Line_As_Priced()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var pricedVariant = Guid.CreateVersion7();
        var unpricedVariant = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, unit, [pricedVariant, unpricedVariant]);
        h.Catalog.RegisterLeaf(pricedVariant, unit);
        h.Catalog.RegisterLeaf(unpricedVariant, unit);
        h.Prices.Add(pricedVariant, price: 2.00m, quantity: 500m, unitId: unit); // $0.004/unit
        // unpricedVariant has no price at all — should not affect the line's own completeness.

        var recipe = BuildSingleIngredientRecipe(parentId, 250m, unit, defaultServings: 4);
        var result = await h.Service.ComputeAsync(recipe, desiredServings: 4);

        // One usable variant price IS a price — the recipe-level completeness is Full (only one
        // costable line, and it priced), not Partial. A partially-priced variant SET is not the same
        // as a partially-priced ingredient LIST.
        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(1, result.PricedCount);
        Assert.Equal(1, result.CostableCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public async Task Parent_Line_In_ComputeExpandedAsync_Rolls_Up_The_Same_Way()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, unit, [variantId]);
        h.Catalog.RegisterLeaf(variantId, unit);
        h.Prices.Add(variantId, price: 2.00m, quantity: 500m, unitId: unit); // $0.004/unit

        var lines = new List<EffectiveIngredient> { new(parentId, 250m, unit) };
        var result = await h.Service.ComputeExpandedAsync(lines, defaultServings: 4, desiredServings: 4);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.25m, result.Amount!.Value, precision: 6);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public async Task ComputeAsync_Calls_FindManyWithVariantsAsync_Exactly_Once_Per_Compute()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var flourId = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        h.Catalog.RegisterLeaf(flourId, unit);
        h.Catalog.RegisterParent(parentId, unit, [variantId]);
        h.Catalog.RegisterLeaf(variantId, unit);
        h.Prices.Add(flourId, price: 1.00m, quantity: 100m, unitId: unit);
        h.Prices.Add(variantId, price: 2.00m, quantity: 100m, unitId: unit);

        var recipe = Recipe.Create(Household, "Multi", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 100m, unit, null, 0),
            new IngredientLine(parentId, 100m, unit, null, 1),
        ], Clock);

        await h.Service.ComputeAsync(recipe, desiredServings: 4);

        Assert.Equal(1, h.Catalog.FindManyWithVariantsCallCount);
    }

    [Fact]
    public async Task ComputeAsync_Memoizes_Price_Fetch_For_A_Variant_Shared_By_Several_Lines()
    {
        var h = new Harness();
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        h.Catalog.RegisterParent(parentId, unit, [variantId]);
        h.Catalog.RegisterLeaf(variantId, unit);
        h.Prices.Add(variantId, price: 2.00m, quantity: 500m, unitId: unit);

        // Two ingredient lines both resolve to the same price ref (the variant): one references the
        // variant directly, the other references the parent (whose only variant is the same ref).
        var recipe = Recipe.Create(Household, "Shared Ref", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(variantId, 100m, unit, null, 0),
            new IngredientLine(parentId, 100m, unit, null, 1),
        ], Clock);

        await h.Service.ComputeAsync(recipe, desiredServings: 4);

        // Fetched once per compute call, even though two lines draw from the same ref.
        Assert.Equal(1, h.Prices.CallCount(variantId));
    }
}

/// <summary>
/// L1 tests for <see cref="CostingService.Compute"/> — the pure overload that accepts
/// pre-loaded price/converter data and issues zero further round-trips (ADR-021 rule 1).
/// Verifies byte-identical figures vs the async path across the same scenario set.
/// </summary>
public sealed class CostingServicePureOverloadTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    // Empty catalog lookup — every scenario below is a leaf product, and a product id absent from
    // catalogById is treated as a leaf (self) by CostingService.PriceRefsFor, matching prior behaviour.
    private static readonly IReadOnlyDictionary<Guid, CatalogProduct> EmptyCatalog =
        new Dictionary<Guid, CatalogProduct>();

    // Identity converter — same unit → same amount; different unit → fail.
    private static Result<decimal> IdentityConverter(Guid _, decimal amount, Guid from, Guid to) =>
        from == to
            ? Result<decimal>.Success(amount)
            : Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No path."));

    private static PricePoint MakePrice(Guid productId, decimal price, decimal qty, Guid unitId, decimal? unitPrice = null) =>
        new(productId, price, qty, unitId, unitPrice);

    private static Recipe BuildSingleIngredientRecipe(Guid productId, decimal quantity, Guid unitId, int defaultServings = 4)
    {
        var recipe = Recipe.Create(Household, "Test", defaultServings, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, quantity, unitId, null, 0)], Clock);
        return recipe;
    }

    // ── Full when all costable ingredients are priced ─────────────────────────

    [Fact]
    public void Pure_Full_When_All_Priced()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        // $2.00 for 500 units → $0.004/unit
        var prices = new Dictionary<Guid, PricePoint>
        {
            [productId] = MakePrice(productId, 2.00m, 500m, unit),
        };

        // Recipe: 250 for 4 servings → cost = 250 × $0.004 = $1.00 / 4 = $0.25/serving
        var recipe = BuildSingleIngredientRecipe(productId, 250m, unit, 4);
        var svc = new CostingService(null!, null!, null!); // ports unused by pure overload
        var result = svc.Compute(recipe, 4, EmptyCatalog, prices, IdentityConverter);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.25m, result.Amount!.Value, precision: 6);
        Assert.Equal(1, result.PricedCount);
        Assert.Equal(1, result.CostableCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public void Pure_Full_Uses_UnitPrice_When_Available()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        // UnitPrice: $0.005/unit; recipe: 200 for 4 servings → $1.00 total / 4 = $0.25/serving
        var prices = new Dictionary<Guid, PricePoint>
        {
            [productId] = MakePrice(productId, 999m, 1m, unit, unitPrice: 0.005m),
        };

        var recipe = BuildSingleIngredientRecipe(productId, 200m, unit, 4);
        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 4, EmptyCatalog, prices, IdentityConverter);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.25m, result.Amount!.Value, precision: 6);
    }

    [Fact]
    public void Pure_Full_Scales_With_DesiredServings()
    {
        var unit = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        // $1.00 for 100 → $0.01/unit; recipe: 100 at 2 default servings
        // At 4 servings: scaled = 200, cost = $2.00, per serving = $0.50
        var prices = new Dictionary<Guid, PricePoint>
        {
            [productId] = MakePrice(productId, 1.00m, 100m, unit),
        };

        var recipe = BuildSingleIngredientRecipe(productId, 100m, unit, 2);
        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 4, EmptyCatalog, prices, IdentityConverter);

        Assert.Equal(0.50m, result.Amount!.Value, precision: 6);
    }

    // ── Partial when some priced ──────────────────────────────────────────────

    [Fact]
    public void Pure_Partial_When_Some_Ingredients_Have_No_Price()
    {
        var unit = Guid.CreateVersion7();
        var flourId = Guid.CreateVersion7();
        var sugarId = Guid.CreateVersion7(); // no price

        var prices = new Dictionary<Guid, PricePoint>
        {
            [flourId] = MakePrice(flourId, 1.00m, 1000m, unit),
        };

        var recipe = Recipe.Create(Household, "Cake", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),
            new IngredientLine(sugarId, 100m, unit, null, 1),
        ], Clock);

        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 4, EmptyCatalog, prices, IdentityConverter);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
        Assert.Equal(1, result.PricedCount);
        Assert.Equal(2, result.CostableCount);
        Assert.Contains(sugarId, result.MissingPriceProductIds);
    }

    // ── None when nothing priced ──────────────────────────────────────────────

    [Fact]
    public void Pure_None_When_No_Costable_Ingredient_Has_Price()
    {
        var unit = Guid.CreateVersion7();
        var flourId = Guid.CreateVersion7();
        var prices = new Dictionary<Guid, PricePoint>(); // empty

        var recipe = BuildSingleIngredientRecipe(flourId, 200m, unit, 4);
        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 4, EmptyCatalog, prices, IdentityConverter);

        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Null(result.Amount);
        Assert.Equal(0, result.PricedCount);
    }

    // ── Untracked staple excluded ────────────────────────────────────────────

    [Fact]
    public void Pure_Untracked_Staple_Excluded_From_CostableCount()
    {
        var unit = Guid.CreateVersion7();
        var flourId = Guid.CreateVersion7();
        var saltId = Guid.CreateVersion7(); // untracked (null qty/unit)

        var prices = new Dictionary<Guid, PricePoint>
        {
            [flourId] = MakePrice(flourId, 1.00m, 1000m, unit),
        };

        var recipe = Recipe.Create(Household, "Bread", 4, Clock).Value;
        recipe.ReplaceIngredients([
            new IngredientLine(flourId, 200m, unit, null, 0),
            new IngredientLine(saltId, null, null, null, 1),
        ], Clock);

        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 4, EmptyCatalog, prices, IdentityConverter);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(1, result.CostableCount);
        Assert.Empty(result.MissingPriceProductIds);
    }

    // ── Converter failure → treated as un-priced ─────────────────────────────

    [Fact]
    public void Pure_Converter_Failure_Treats_Ingredient_As_Unpriced()
    {
        var priceUnit = Guid.CreateVersion7();
        var ingredientUnit = Guid.CreateVersion7(); // different → converter fails
        var productId = Guid.CreateVersion7();

        var prices = new Dictionary<Guid, PricePoint>
        {
            // price is in priceUnit; ingredient is in ingredientUnit → conversion will fail
            [productId] = MakePrice(productId, 1.00m, 100m, priceUnit),
        };

        var recipe = BuildSingleIngredientRecipe(productId, 100m, ingredientUnit, 4);
        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 4, EmptyCatalog, prices, IdentityConverter);

        // Converter fails → treated as un-priced → None
        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Null(result.Amount);
        Assert.Contains(productId, result.MissingPriceProductIds);
    }

    // ── DM-19: parent → variant rollup, pure overload (byte-identical to the async path) ────────

    [Fact]
    public void Pure_Parent_With_One_Priced_Variant_Prices_The_Line()
    {
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var catalogById = new Dictionary<Guid, CatalogProduct>
        {
            [parentId] = new(parentId, "Parent", TrackStock: true, unit, null, IsParent: true, [variantId]),
        };
        var prices = new Dictionary<Guid, PricePoint>
        {
            [variantId] = MakePrice(variantId, 2.00m, 500m, unit), // $0.004/unit
        };

        var recipe = BuildSingleIngredientRecipe(parentId, 250m, unit, 4);
        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 4, catalogById, prices, IdentityConverter);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.25m, result.Amount!.Value, precision: 6);
        Assert.Empty(result.MissingPriceProductIds);
    }

    [Fact]
    public void Pure_Parent_No_Variant_Priced_Parent_Own_Id_In_Missing()
    {
        var unit = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var catalogById = new Dictionary<Guid, CatalogProduct>
        {
            [parentId] = new(parentId, "Parent", TrackStock: true, unit, null, IsParent: true, [variantId]),
        };
        var prices = new Dictionary<Guid, PricePoint>(); // no variant priced

        var recipe = BuildSingleIngredientRecipe(parentId, 100m, unit, 1);
        var svc = new CostingService(null!, null!, null!);
        var result = svc.Compute(recipe, 1, catalogById, prices, IdentityConverter);

        Assert.Equal(CostCompleteness.None, result.Completeness);
        Assert.Contains(parentId, result.MissingPriceProductIds);
        Assert.DoesNotContain(variantId, result.MissingPriceProductIds);
    }
}
