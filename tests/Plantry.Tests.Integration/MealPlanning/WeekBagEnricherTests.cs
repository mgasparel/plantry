using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// Unit-style tests for <see cref="WeekBagEnricher"/> that live in the Integration project
/// because <see cref="WeekBagEnricher"/> is internal to Plantry.Web (InternalsVisibleTo).
/// These tests exercise the adapter layer — building stock/catalog/price/converter from WeekBag
/// facts and feeding them into the pure domain compute overloads — without touching the database.
/// </summary>
public sealed class WeekBagEnricherTests
{
    // ── Stable identifiers ────────────────────────────────────────────────────────

    private static readonly Guid RecipeId   = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProductId  = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Ing1Id     = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    // Two units: grams (base) and kilograms (1 kg = 1000 g).
    private static readonly Guid GramUnitId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid KgUnitId   = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="WeekBagEnricher"/> using null-returning port fakes — safe because
    /// we only exercise the pure <c>Compute</c> overloads which accept all data as parameters
    /// and never call the injected port services.
    /// </summary>
    private static WeekBagEnricher MakeEnricher(WeekBag bag) =>
        new(bag,
            new FulfillmentService(new NullStockReader(), new NullCatalogReader(), new NullConverter()),
            new CostingService(new NullPriceReader(), new NullConverter()),
            SystemClock.Instance);

    // ── Multi-unit lot summing ────────────────────────────────────────────────────

    /// <summary>
    /// When a product has two stock lots in different units (e.g. 300 g + 0.5 kg = 800 g),
    /// BuildStockById must sum them both converted to the product's default unit (grams).
    /// The total (800 g) must exceed the required quantity (500 g at scale=1), so the
    /// ingredient reports InStock → 100 % fulfillment.
    ///
    /// The old (incorrect) implementation would have picked only the default-unit lot (300 g),
    /// which is below the required 500 g → Low or Missing → wrong fulfillment %.
    /// </summary>
    [Fact(DisplayName = "BuildStockById sums multi-unit lots into the default unit — 300g + 0.5kg = 800g → InStock")]
    public void MultiUnitLots_SummedIntoDefaultUnit_YieldsCorrectFulfillment()
    {
        // Recipe: 1 ingredient, 500 g, defaultServings=1.
        // Desired servings = 1 → scale = 1.0 → required = 500 g.
        var bag = new WeekBag(
            recipes: new Dictionary<Guid, RecipeFact>
            {
                [RecipeId] = new RecipeFact(RecipeId, "Test Recipe", DefaultServings: 1),
            },
            ingredientsByRecipe: new Dictionary<Guid, IReadOnlyList<IngredientFact>>
            {
                [RecipeId] = [new IngredientFact(Ing1Id, RecipeId, ProductId, Quantity: 500m, UnitId: GramUnitId, Ordinal: 0)],
            },
            products: new Dictionary<Guid, ProductFact>
            {
                [ProductId] = new ProductFact(
                    ProductId, "Flour",
                    TrackStock: true,
                    DefaultUnitId: GramUnitId,
                    ParentProductId: null,
                    HasVariants: false,
                    Archived: false,
                    VariantProductIds: []),
            },
            conversionsByProduct: new Dictionary<Guid, IReadOnlyList<ConversionFact>>
            {
                // 1 g → 0.001 kg  (factor = 0.001)  ⟺  1 kg → 1000 g
                [ProductId] = [new ConversionFact(ProductId, GramUnitId, KgUnitId, 0.001m)],
            },
            units: new Dictionary<Guid, UnitFact>
            {
                [GramUnitId] = new UnitFact(GramUnitId, "g",  "grams",     "mass", FactorToBase: 1m,    IsBase: true),
                [KgUnitId]   = new UnitFact(KgUnitId,   "kg", "kilograms", "mass", FactorToBase: 1000m, IsBase: false),
            },
            stockByProduct: new Dictionary<Guid, StockFact>
            {
                // 300 g (default unit) + 0.5 kg = 300 g + 500 g = 800 g total.
                // Required = 500 g → InStock.
                [ProductId] = new StockFact(
                    ProductId,
                    Lots:
                    [
                        new StockLotFact(ProductId, GramUnitId, 300m),   // 300 g
                        new StockLotFact(ProductId, KgUnitId,   0.5m),   // 0.5 kg = 500 g
                    ],
                    SoonestExpiry: null),
            },
            latestPriceByProduct: new Dictionary<Guid, PriceFact>());

        var enricher = MakeEnricher(bag);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var result = enricher.Enrich(RecipeId, servings: 1, today);

        Assert.NotNull(result);
        Assert.Equal(100, result.FulfillmentPercent); // 1/1 tracked ingredient InStock = 100 %
        Assert.Null(result.TotalCost);               // no price data
        Assert.False(result.HasExpiringIngredients);
    }

    /// <summary>
    /// When a product has only a non-default-unit lot (0.4 kg = 400 g),
    /// the converter must bridge it into grams. With required = 500 g, 400 g &lt; 500 g → Low (not Missing).
    /// Previously the old implementation would have picked the only lot and passed it in its own
    /// unit (kg) to FulfillmentService, then FulfillmentService would convert 0.4 kg → 400 g using
    /// the converter; the result is the same only when there is exactly ONE lot. This test confirms
    /// the behaviour is stable for that single-non-default-unit case too.
    /// </summary>
    [Fact(DisplayName = "BuildStockById converts non-default-unit lot to default — 0.4kg = 400g → Low (400g < 500g required)")]
    public void SingleNonDefaultUnitLot_ConvertedToDefault_YieldsCorrectFulfillment()
    {
        var bag = new WeekBag(
            recipes: new Dictionary<Guid, RecipeFact>
            {
                [RecipeId] = new RecipeFact(RecipeId, "Test Recipe", DefaultServings: 1),
            },
            ingredientsByRecipe: new Dictionary<Guid, IReadOnlyList<IngredientFact>>
            {
                [RecipeId] = [new IngredientFact(Ing1Id, RecipeId, ProductId, Quantity: 500m, UnitId: GramUnitId, Ordinal: 0)],
            },
            products: new Dictionary<Guid, ProductFact>
            {
                [ProductId] = new ProductFact(
                    ProductId, "Flour",
                    TrackStock: true,
                    DefaultUnitId: GramUnitId,
                    ParentProductId: null,
                    HasVariants: false,
                    Archived: false,
                    VariantProductIds: []),
            },
            conversionsByProduct: new Dictionary<Guid, IReadOnlyList<ConversionFact>>
            {
                [ProductId] = [new ConversionFact(ProductId, GramUnitId, KgUnitId, 0.001m)],
            },
            units: new Dictionary<Guid, UnitFact>
            {
                [GramUnitId] = new UnitFact(GramUnitId, "g",  "grams",     "mass", FactorToBase: 1m,    IsBase: true),
                [KgUnitId]   = new UnitFact(KgUnitId,   "kg", "kilograms", "mass", FactorToBase: 1000m, IsBase: false),
            },
            stockByProduct: new Dictionary<Guid, StockFact>
            {
                // Only 0.4 kg = 400 g. Required = 500 g → Low (not Missing, not InStock).
                [ProductId] = new StockFact(
                    ProductId,
                    Lots: [new StockLotFact(ProductId, KgUnitId, 0.4m)],
                    SoonestExpiry: null),
            },
            latestPriceByProduct: new Dictionary<Guid, PriceFact>());

        var enricher = MakeEnricher(bag);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var result = enricher.Enrich(RecipeId, servings: 1, today);

        Assert.NotNull(result);
        // 1 tracked ingredient, 0 fully InStock (Low) → 0 % fulfillment.
        Assert.Equal(0, result.FulfillmentPercent);
    }

    // ── Null-returning port fakes ─────────────────────────────────────────────────
    // These are safe stubs — WeekBagEnricher only calls the pure Compute overloads
    // which receive all data as parameters and never invoke the injected services.

    private sealed class NullStockReader : IInventoryStockReader
    {
        public Task<ProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<ProductStock?>(null);
        public Task<IReadOnlyDictionary<Guid, ProductStock>> FindStockBatchAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ProductStock>>(new Dictionary<Guid, ProductStock>());
    }

    private sealed class NullCatalogReader : ICatalogProductReader
    {
        public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<CatalogProduct?>(null);
        public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);
        public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CatalogProductSummary>>(new Dictionary<Guid, CatalogProductSummary>());
        public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);

        public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);

        public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
    }

    private sealed class NullConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            Task.FromResult(Result<decimal>.Success(amount));
    }

    private sealed class NullPriceReader : IPriceReader
    {
        public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<PricePoint?>(null);
    }
}
