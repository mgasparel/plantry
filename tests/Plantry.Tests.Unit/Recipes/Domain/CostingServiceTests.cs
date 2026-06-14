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

        /// <summary>Registers a price: <paramref name="price"/> for <paramref name="quantity"/> of <paramref name="unitId"/>.</summary>
        public void Add(Guid productId, decimal price, decimal quantity, Guid unitId, decimal? unitPrice = null) =>
            _prices[productId] = new PricePoint(productId, price, quantity, unitId, unitPrice);

        public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_prices.GetValueOrDefault(productId));
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

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public FakePriceReader Prices { get; } = new();
        public FakeUnitConverter Converter { get; } = new();
        public CostingService Service => new(Prices, Converter);
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
}
