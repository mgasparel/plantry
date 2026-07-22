using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.MealPlanning;
using Plantry.Web.Recipes;
using CostCompleteness = Plantry.Recipes.Domain.CostCompleteness;

namespace Plantry.Tests.Web;

/// <summary>
/// L4/L5 deal-aware costing tests (P5-9b, DJ6) — proves the Web price adapters flip the recipe and
/// meal-plan cost paths off the deal-blind (C7) reader onto Pricing's clock-driven effective-price read.
///
/// Both <see cref="PriceReaderAdapter"/> (Recipes) and <see cref="MealPlanPriceReaderAdapter"/>
/// (MealPlanning) are composed over a <b>real</b> <see cref="PricingQueries"/> plus a window-aware fake
/// <see cref="IPriceObservationRepository"/> and a mutable <see cref="IClock"/>. The same data is costed
/// twice against two "today" values: a recipe / meal-plan cost reflects an active deal on an ingredient
/// while its window contains today, and reverts to the latest purchase once the clock advances past the
/// window — never storing the effective price (ADR-010: the read lives in Pricing; the consumers never
/// depend on Deals).
/// </summary>
public sealed class DealAwareCostingAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UnitId = Guid.CreateVersion7();

    private static readonly DateOnly DealFrom = new(2026, 7, 1);
    private static readonly DateOnly DealTo = new(2026, 7, 7);
    private static readonly DateTimeOffset InWindow = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterWindow = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private const decimal PurchaseUnitPrice = 5.00m;
    private const decimal DealUnitPrice = 2.00m;

    // ── L4: recipe cost reflects an active deal and reverts when the window lapses ──────────────────

    [Fact(DisplayName = "L4: recipe cost uses the active deal price while the deal window contains today")]
    public async Task Recipe_Cost_Reflects_Active_Deal_In_Window()
    {
        var productId = Guid.CreateVersion7();
        var repo = SeededRepo(productId);
        var clock = new MutableClock(InWindow);
        var costing = new CostingService(
            new PriceReaderAdapter(new PricingQueries(repo), clock),
            new IdentityUnitConverter());

        var cost = await costing.ComputeAsync(SingleIngredientRecipe(productId), desiredServings: 1);

        Assert.Equal(CostCompleteness.Full, cost.Completeness);
        Assert.Equal(DealUnitPrice, cost.Amount);
    }

    [Fact(DisplayName = "L4: recipe cost reverts to the latest purchase once the deal window lapses")]
    public async Task Recipe_Cost_Reverts_To_Purchase_When_Window_Lapses()
    {
        var productId = Guid.CreateVersion7();
        var repo = SeededRepo(productId);
        var clock = new MutableClock(AfterWindow); // deal expired; only latest purchase remains buyable
        var costing = new CostingService(
            new PriceReaderAdapter(new PricingQueries(repo), clock),
            new IdentityUnitConverter());

        var cost = await costing.ComputeAsync(SingleIngredientRecipe(productId), desiredServings: 1);

        Assert.Equal(CostCompleteness.Full, cost.Completeness);
        Assert.Equal(PurchaseUnitPrice, cost.Amount);
    }

    [Fact(DisplayName = "L4: recipe cost is Full when one ingredient is priced only via a Manual observation and the other is priced normally (plantry-3fqm)")]
    public async Task Recipe_Cost_Is_Full_When_An_Ingredient_Is_Priced_Only_Manually()
    {
        var manualProductId = Guid.CreateVersion7();
        var purchaseProductId = Guid.CreateVersion7();
        const decimal manualUnitPrice = 2.50m;

        var repo = new WindowAwarePriceRepo();
        // Seeded pantry stock (plantry-3fqm's motivating scenario) — no receipt behind it, only a
        // household-entered Manual observation.
        repo.Add(PriceObservation.Record(
            Household, manualProductId, null, price: manualUnitPrice, quantity: 1m, unitId: UnitId,
            unitPrice: manualUnitPrice, source: PriceSource.Manual, merchantText: null,
            sourceRef: null, observedAt: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            userId: Guid.CreateVersion7()));
        repo.Add(PriceObservation.Record(
            Household, purchaseProductId, null, price: PurchaseUnitPrice, quantity: 1m, unitId: UnitId,
            unitPrice: PurchaseUnitPrice, source: PriceSource.Purchase, merchantText: "Superstore",
            sourceRef: Guid.CreateVersion7(), observedAt: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            userId: Guid.CreateVersion7()));

        var clock = new MutableClock(InWindow);
        var costing = new CostingService(
            new PriceReaderAdapter(new PricingQueries(repo), clock),
            new IdentityUnitConverter());

        var recipe = Recipe.Create(Household, "Manual Price Test Recipe", defaultServings: 1, SystemClock.Instance).Value;
        recipe.ReplaceIngredients(
            [
                new IngredientLine(manualProductId, 1m, UnitId, null, 0),
                new IngredientLine(purchaseProductId, 1m, UnitId, null, 1),
            ],
            SystemClock.Instance);

        var cost = await costing.ComputeAsync(recipe, desiredServings: 1);

        Assert.Equal(CostCompleteness.Full, cost.Completeness);
        Assert.Empty(cost.MissingPriceProductIds);
        Assert.Equal(manualUnitPrice + PurchaseUnitPrice, cost.Amount);
    }

    [Fact(DisplayName = "ADR-023 A7: PriceReaderAdapter (IPriceReader) never surfaces a superseded observation")]
    public async Task PriceReaderAdapter_Excludes_Superseded_Observation()
    {
        var productId = Guid.CreateVersion7();
        var repo = new WindowAwarePriceRepo();
        var original = PriceObservation.Record(
            Household, productId, null, price: PurchaseUnitPrice, quantity: 1m, unitId: UnitId,
            unitPrice: PurchaseUnitPrice, source: PriceSource.Purchase, merchantText: "Superstore",
            sourceRef: Guid.CreateVersion7(), observedAt: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            userId: Guid.CreateVersion7());
        const decimal amendedUnitPrice = 1.33m;
        var amendment = PriceObservation.RecordAmendment(original, correctedQuantity: 3m, unitPrice: amendedUnitPrice, Guid.CreateVersion7());
        original.Supersede(amendment.Id);
        repo.Add(original);
        repo.Add(amendment);

        var reader = new PriceReaderAdapter(new PricingQueries(repo), new MutableClock(InWindow));
        var point = await reader.FindLatestAsync(productId);

        Assert.NotNull(point);
        Assert.Equal(amendedUnitPrice, point.UnitPrice); // the live amendment, never the superseded original
    }

    // ── L5: meal-plan cost reflects the same active deal and reverts on lapse ───────────────────────

    [Fact(DisplayName = "L5: meal-plan cost uses the active deal price while the deal window contains today")]
    public async Task MealPlan_Cost_Reflects_Active_Deal_In_Window()
    {
        var productId = Guid.CreateVersion7();
        var repo = SeededRepo(productId);
        var clock = new MutableClock(InWindow);
        var planCosting = new PlanCostingService(
            new UnusedRecipeReadModel(),
            new MealPlanPriceReaderAdapter(new PricingQueries(repo), clock));

        var result = await planCosting.RollUpMealAsync(ProductDishMeal(productId));

        Assert.Equal(Plantry.MealPlanning.Domain.CostCompleteness.Full, result.Completeness);
        Assert.Equal(DealUnitPrice, result.Amount); // servings = 1 → cost == effective unit price
    }

    [Fact(DisplayName = "L5: meal-plan cost reverts to the latest purchase once the deal window lapses")]
    public async Task MealPlan_Cost_Reverts_To_Purchase_When_Window_Lapses()
    {
        var productId = Guid.CreateVersion7();
        var repo = SeededRepo(productId);
        var clock = new MutableClock(AfterWindow);
        var planCosting = new PlanCostingService(
            new UnusedRecipeReadModel(),
            new MealPlanPriceReaderAdapter(new PricingQueries(repo), clock));

        var result = await planCosting.RollUpMealAsync(ProductDishMeal(productId));

        Assert.Equal(Plantry.MealPlanning.Domain.CostCompleteness.Full, result.Completeness);
        Assert.Equal(PurchaseUnitPrice, result.Amount);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    /// <summary>A repo carrying one latest purchase and one in-window deal for the product.</summary>
    private static WindowAwarePriceRepo SeededRepo(Guid productId)
    {
        var repo = new WindowAwarePriceRepo();
        repo.Add(PriceObservation.Record(
            Household, productId, null, price: PurchaseUnitPrice, quantity: 1m, unitId: UnitId,
            unitPrice: PurchaseUnitPrice, source: PriceSource.Purchase, merchantText: "Superstore",
            sourceRef: Guid.CreateVersion7(), observedAt: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            userId: Guid.CreateVersion7()));
        repo.Add(PriceObservation.Record(
            Household, productId, null, price: DealUnitPrice, quantity: 1m, unitId: UnitId,
            unitPrice: DealUnitPrice, source: PriceSource.Deal, merchantText: "Flyer",
            sourceRef: Guid.CreateVersion7(), observedAt: new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero),
            userId: Guid.CreateVersion7(), validFrom: DealFrom, validTo: DealTo));
        return repo;
    }

    private static Recipe SingleIngredientRecipe(Guid productId)
    {
        var recipe = Recipe.Create(Household, "Deal Test Recipe", defaultServings: 1, SystemClock.Instance).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, 1m, UnitId, null, 0)], SystemClock.Instance);
        return recipe;
    }

    private static PlannedMeal ProductDishMeal(Guid productId)
    {
        var plan = MealPlan.Start(Household, new DateOnly(2026, 7, 6), SystemClock.Instance);
        plan.AssignMeal(
            new DateOnly(2026, 7, 6), MealSlotId.New(),
            [new DishSpec(DishKind.Product, productId, 1)], null, "manual", Guid.NewGuid(), SystemClock.Instance);
        return plan.PlannedMeals[0];
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>Clock whose "now" can be moved to drive the deal-window evaluation.</summary>
    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>
    /// In-memory repo that evaluates the deal window and source filter itself (mirroring the DB read
    /// model), so the same seeded data yields the deal in-window and the purchase once it lapses.
    /// </summary>
    private sealed class WindowAwarePriceRepo : IPriceObservationRepository
    {
        private readonly List<PriceObservation> _items = [];

        public void Add(PriceObservation observation) => _items.Add(observation);

        public Task<PriceObservation?> CheapestActiveDealForProductAsync(
            Guid productId, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult(_items
                .Where(o => o.ProductId == productId
                    && o.Source == PriceSource.Deal
                    && o.ValidFrom <= today && today <= o.ValidTo
                    && o.SupersededById is null) // ADR-023 A7
                .OrderBy(o => o.UnitPrice)
                .FirstOrDefault());

        public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult(_items
                .Where(o => o.ProductId == productId
                    && (o.Source == PriceSource.Purchase || o.Source == PriceSource.Manual)
                    && o.SupersededById is null) // ADR-023 A7
                .OrderByDescending(o => o.ObservedAt)
                .FirstOrDefault());

        public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
            Task.FromResult<PriceObservation?>(null);

        public Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default) =>
            Task.FromResult(_items.FirstOrDefault(o => o.Id == id));

        public Task AddAsync(PriceObservation observation, CancellationToken ct = default)
        {
            _items.Add(observation);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PriceObservation>>([]);
    }

    /// <summary>Identity unit converter — the recipe ingredient and price share a unit here.</summary>
    private sealed class IdentityUnitConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            Task.FromResult(fromUnitId == toUnitId
                ? Result<decimal>.Success(amount)
                : Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No conversion path.")));
    }

    /// <summary>Recipe read model never consulted — the meal-plan tests cost a product dish only.</summary>
    private sealed class UnusedRecipeReadModel : IRecipeReadModel
    {
        public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<RecipeReadModel?>(null);
        public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);
        public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
            => Task.FromResult<RecipeDishEnrichment?>(null);
        public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);
        public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
