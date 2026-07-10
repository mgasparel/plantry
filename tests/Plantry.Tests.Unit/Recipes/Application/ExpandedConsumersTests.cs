using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using InventoryProductStock = Plantry.Recipes.Application.ProductStock;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// Tests for the recipe-composition (inclusions) expanded-view consumers (recipe-composition.md §7, D4/D14,
/// plantry-fqb0.5): <see cref="ExpandedLineAggregation"/>, and the expanded overloads of
/// <see cref="RecipeShortfallCalculator"/>, <see cref="CostingService"/>, and <see cref="FulfillmentService"/>.
///
/// Exercises the full expand → aggregate → compute path over real parent/sub recipes so a parent's shortfall,
/// cost, and cookability reflect its included recipes' products (scaled by the inclusion factor), with
/// duplicate subs merged by <c>(ProductId, UnitId)</c>. Flat behaviour is proven unchanged by the untouched
/// flat-overload regression suites (<see cref="RecipeShortfallCalculatorTests"/>, <c>CostingServiceTests</c>,
/// <c>FulfillmentServiceTests</c>).
/// </summary>
public sealed class ExpandedConsumersTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly DateOnly Today = new(2026, 7, 10);
    private static readonly Guid Unit = Guid.CreateVersion7();

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakePriceReader : IPriceReader
    {
        private readonly Dictionary<Guid, PricePoint> _prices = [];
        public void Add(Guid productId, decimal unitPrice, Guid unitId) =>
            _prices[productId] = new PricePoint(productId, unitPrice, 1m, unitId, unitPrice);
        public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_prices.GetValueOrDefault(productId));
    }

    private sealed class FakeStockReader : IInventoryStockReader
    {
        private readonly Dictionary<Guid, InventoryProductStock> _stock = [];
        public void Add(Guid productId, decimal available, Guid unitId) =>
            _stock[productId] = new InventoryProductStock(productId, available, unitId, null);
        public Task<InventoryProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_stock.GetValueOrDefault(productId));
        public Task<IReadOnlyDictionary<Guid, InventoryProductStock>> FindStockBatchAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, InventoryProductStock>>(
                productIds.Where(_stock.ContainsKey).ToDictionary(id => id, id => _stock[id]));
    }

    private sealed class IdentityConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            Task.FromResult(fromUnitId == toUnitId
                ? Result<decimal>.Success(amount)
                : Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No path.")));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Recipe Seed(
        FakeRecipeRepository repo, string name, int defaultServings,
        IReadOnlyList<IngredientLine> ingredients, IReadOnlyList<InclusionLine>? inclusions = null)
    {
        var recipe = Recipe.Create(Household, name, defaultServings, Clock).Value;
        var result = recipe.ReplaceLines(ingredients, inclusions ?? [], Clock);
        Assert.True(result.IsSuccess, $"Seed failed: {result.Error.Code}");
        repo.Items.Add(recipe);
        return recipe;
    }

    private static async Task<IReadOnlyList<EffectiveIngredient>> ExpandAndAggregateAsync(
        FakeRecipeRepository repo, RecipeId recipeId)
    {
        var expansion = new RecipeExpansionService(repo);
        var result = await expansion.ExpandAsync(recipeId);
        Assert.True(result.IsSuccess, $"Expansion failed: {result.Error.Code}");
        return result.Value.AggregateByProductAndUnit();
    }

    // ══ Aggregation (D14) ════════════════════════════════════════════════════════

    [Fact(DisplayName = "Aggregation merges duplicate (ProductId, UnitId) lines and sums their quantities (D14)")]
    public void Aggregate_Merges_Duplicate_Product_Unit()
    {
        var productA = Guid.CreateVersion7();
        var inc1 = InclusionId.New();
        var inc2 = InclusionId.New();

        // Same product A, same unit, reached via two distinct inclusion paths (duplicate sub, D14).
        IReadOnlyList<ExpandedLine> lines =
        [
            new ExpandedLine([inc1], IngredientId.New(), RecipeId.New(), productA, 150m, Unit, []),
            new ExpandedLine([inc2], IngredientId.New(), RecipeId.New(), productA, 50m, Unit, []),
        ];

        var aggregated = lines.AggregateByProductAndUnit();

        var line = Assert.Single(aggregated);
        Assert.Equal(productA, line.ProductId);
        Assert.Equal(200m, line.Quantity);   // 150 + 50 merged into one row
        Assert.Equal(Unit, line.UnitId);
    }

    [Fact(DisplayName = "Aggregation keeps the same product in different units as separate rows")]
    public void Aggregate_Keeps_Distinct_Units_Separate()
    {
        var productA = Guid.CreateVersion7();
        var otherUnit = Guid.CreateVersion7();

        IReadOnlyList<ExpandedLine> lines =
        [
            new ExpandedLine([], IngredientId.New(), RecipeId.New(), productA, 100m, Unit, []),
            new ExpandedLine([], IngredientId.New(), RecipeId.New(), productA, 5m, otherUnit, []),
        ];

        var aggregated = lines.AggregateByProductAndUnit();

        Assert.Equal(2, aggregated.Count);
        Assert.Equal(2, aggregated.Select(l => l.UnitId).Distinct().Count());
    }

    [Fact(DisplayName = "Aggregation passes untracked staples through as one null-qty/unit row per product")]
    public void Aggregate_Passes_Untracked_Staples_Through()
    {
        var staple = Guid.CreateVersion7();

        IReadOnlyList<ExpandedLine> lines =
        [
            new ExpandedLine([InclusionId.New()], IngredientId.New(), RecipeId.New(), staple, null, null, []),
            new ExpandedLine([InclusionId.New()], IngredientId.New(), RecipeId.New(), staple, null, null, []),
        ];

        var aggregated = lines.AggregateByProductAndUnit();

        var line = Assert.Single(aggregated);
        Assert.Equal(staple, line.ProductId);
        Assert.Null(line.Quantity);
        Assert.Null(line.UnitId);
    }

    // ══ Shortfall over the expanded view (AC: counts sub products scaled) ═════════

    [Fact(DisplayName = "Shortfall for a parent with an inclusion counts the sub's product, scaled by the factor")]
    public async Task Shortfall_Counts_Sub_Product_Scaled()
    {
        var repo = new FakeRecipeRepository();
        var catalog = new FakeCatalogProductReader();
        var stock = new FakeStockReader();

        var productS = Guid.CreateVersion7();
        catalog.RegisterTracked(productS, "Cashews");
        stock.Add(productS, available: 100m, Unit);   // have 100, sub needs 150 at parent default

        // Sub: default 2 servings, 100 of productS. Parent: default 4, includes 3 servings of the sub
        // → factor 3/2 = 1.5 → expanded quantity = 150.
        var sub = Seed(repo, "Nacho Cheese", 2, [new IngredientLine(productS, 100m, Unit, null, 0)]);
        var parent = Seed(repo, "Nachos", 4, [], [new InclusionLine(sub.Id, 3m, null, 0)]);

        var effective = await ExpandAndAggregateAsync(repo, parent.Id);
        var fulfillmentService = new FulfillmentService(stock, catalog, new IdentityConverter(), new FakeExpiringSoonHorizonReader());
        var fulfillment = await fulfillmentService.ComputeExpandedAsync(effective, parent.DefaultServings, parent.DefaultServings, Today);

        var shortfall = RecipeShortfallCalculator.Compute(effective, fulfillment, parent.DefaultServings, parent.DefaultServings);

        var line = Assert.Single(shortfall);
        Assert.Equal(productS, line.ProductId);
        Assert.Equal(50m, line.ShortfallQuantity);   // need 150 (100 × 1.5), have 100 → short 50
        Assert.Equal(Unit, line.UnitId);
    }

    // ══ Costing over the expanded view (AC: sub cost × factor) ════════════════════

    [Fact(DisplayName = "Costing a parent includes the sub's ingredient cost × the inclusion factor")]
    public async Task Costing_Includes_Sub_Cost_Times_Factor()
    {
        var repo = new FakeRecipeRepository();
        var prices = new FakePriceReader();
        var productS = Guid.CreateVersion7();
        prices.Add(productS, unitPrice: 0.01m, Unit);   // $0.01 per unit

        // Sub default 2, 100 of productS. Parent default 4, includes 3 servings → factor 1.5 → 150 units.
        var sub = Seed(repo, "Nacho Cheese", 2, [new IngredientLine(productS, 100m, Unit, null, 0)]);
        var parent = Seed(repo, "Nachos", 4, [], [new InclusionLine(sub.Id, 3m, null, 0)]);

        var effective = await ExpandAndAggregateAsync(repo, parent.Id);
        var costing = new CostingService(prices, new IdentityConverter());

        // At default 4 servings: scale 1 → cost = 150 × $0.01 = $1.50 → per serving = $0.375.
        var result = await costing.ComputeExpandedAsync(effective, parent.DefaultServings, parent.DefaultServings);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(0.375m, result.Amount!.Value, precision: 6);
        Assert.Equal(1, result.CostableCount);
        Assert.Equal(1, result.PricedCount);
    }

    // ══ Fulfillment over the expanded view for an inclusion-only recipe (AC5) ═════

    [Fact(DisplayName = "Inclusion-only recipe: fulfillment reflects the sub's products (expanded availability)")]
    public async Task InclusionOnly_Fulfillment_Reflects_Sub_Products()
    {
        var repo = new FakeRecipeRepository();
        var catalog = new FakeCatalogProductReader();
        var stock = new FakeStockReader();

        var productS = Guid.CreateVersion7();
        catalog.RegisterTracked(productS, "Romaine");
        // No stock → the sub's product is Missing, so an inclusion-only parent is not cookable.

        var sub = Seed(repo, "Caesar Base", 2, [new IngredientLine(productS, 50m, Unit, null, 0)]);
        var parent = Seed(repo, "Caesar Deluxe", 2, [], [new InclusionLine(sub.Id, 2m, null, 0)]);

        var effective = await ExpandAndAggregateAsync(repo, parent.Id);
        var fulfillmentService = new FulfillmentService(stock, catalog, new IdentityConverter(), new FakeExpiringSoonHorizonReader());
        var fulfillment = await fulfillmentService.ComputeExpandedAsync(effective, parent.DefaultServings, parent.DefaultServings, Today);

        var line = Assert.Single(fulfillment.Lines);
        Assert.Equal(productS, line.ProductId);
        Assert.Equal(IngredientStatus.Missing, line.Status);
        Assert.False(fulfillment.Overall.FullyCookable);
        Assert.Equal(1, fulfillment.Overall.MissingCount);
    }
}
