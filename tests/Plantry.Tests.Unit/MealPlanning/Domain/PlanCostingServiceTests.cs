using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="PlanCostingService"/>.
/// Uses in-memory fakes — no EF, no DB, no real adapters.
/// </summary>
public sealed class PlanCostingServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 9);
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();

    // ── Note meal → None ──────────────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsNone_ForNoteMeal()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(MealCost.None, result);
    }

    // ── Recipe dish — full cost ────────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsFullCost_WhenRecipeHasCompleteEnrichment()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(100, 6.00m, false, false); // $6 total for 2 servings
        var reader = new FakePriceEnrichmentReader(recipeId, enrichment);

        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(6.00m, result.Amount);
    }

    // ── Recipe dish — partial cost ─────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsPartial_WhenEnrichmentIsPartial()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(80, 4.00m, true, false);
        var reader = new FakePriceEnrichmentReader(recipeId, enrichment);

        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
    }

    // ── Recipe dish — no cost data ────────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsNone_WhenNoEnrichment()
    {
        var reader = new FakePriceEnrichmentReader(Guid.NewGuid(), null);
        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, Guid.NewGuid(), 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(MealCost.None, result);
    }

    // ── Product dish — priced, same unit as observation (AC4) ─────────────────

    [Fact]
    public async Task RollUpMealAsync_ComputesCostForProductDish()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        // Price/Quantity = $10/1 = $10/unit, servings = 3 → expected cost = $30. UnitPrice (2m) is
        // Pricing's per-base-unit figure and must NOT be used directly (plantry-9n7l) — if it were,
        // the (wrong) result would be 2m * 3 = 6m.
        var pricePoint = new MealPlanPricePoint(productId, 10m, 1m, unitId, 2m);
        var fakePriceReader = new FakePriceReader(pricePoint);
        var catalogReader = new FakeMealPlanCatalogProductReader(new Dictionary<Guid, Guid> { [productId] = unitId });

        var svc = BuildService(priceReader: fakePriceReader, catalogReader: catalogReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 3)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(30m, result.Amount);
    }

    // ── Product dish — derived price when UnitPrice is null (AC4) ────────────

    [Fact]
    public async Task RollUpMealAsync_DerivesPriceFromPriceAndQuantity_WhenUnitPriceAbsent()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        // Price = $5 for qty 2 → unit price = $2.50; servings = 2 → expected $5
        var pricePoint = new MealPlanPricePoint(productId, 5m, 2m, unitId, null);
        var fakePriceReader = new FakePriceReader(pricePoint);
        var catalogReader = new FakeMealPlanCatalogProductReader(new Dictionary<Guid, Guid> { [productId] = unitId });

        var svc = BuildService(priceReader: fakePriceReader, catalogReader: catalogReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(5m, result.Amount);
    }

    // ── Product dish — kg-default/kg-priced regression pin (AC1) ─────────────

    [Fact]
    public async Task RollUpMealAsync_ProductDish_UsesPriceOverQuantity_NotBaseUnitPrice_ForMassDefaultUnit()
    {
        var productId = Guid.NewGuid();
        var kgUnitId = Guid.NewGuid();
        // $12.99 for 1 kg → correct unit price = $12.99. UnitPrice here simulates Pricing's
        // per-BASE-unit (per gram) figure for the same observation (12.99 / 1000) — using it
        // directly would understate the cost ~1000x, the exact bug this ticket fixes.
        var pricePoint = new MealPlanPricePoint(productId, 12.99m, 1m, kgUnitId, 0.01299m);
        var fakePriceReader = new FakePriceReader(pricePoint);
        // Product's default unit IS the kg the observation was recorded in — identity, no conversion.
        var catalogReader = new FakeMealPlanCatalogProductReader(new Dictionary<Guid, Guid> { [productId] = kgUnitId });

        var svc = BuildService(priceReader: fakePriceReader, catalogReader: catalogReader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        Assert.Equal(25.98m, result.Amount); // 12.99 * 2, never 0.01299 * 2
    }

    // ── Product dish — conversion applied when price unit differs (AC2) ──────

    [Fact]
    public async Task RollUpMealAsync_ProductDish_ConvertsPriceUnitToDefaultUnit()
    {
        var productId = Guid.NewGuid();
        var eaUnitId = Guid.NewGuid();
        var dozUnitId = Guid.NewGuid();
        // Priced per-ea at $1.00; default unit is doz (12 ea). Expected per-doz price = $12.00.
        var pricePoint = new MealPlanPricePoint(productId, 1.00m, 1m, eaUnitId, null);
        var fakePriceReader = new FakePriceReader(pricePoint);
        var catalogReader = new FakeMealPlanCatalogProductReader(new Dictionary<Guid, Guid> { [productId] = dozUnitId });
        // 1 ea converts to 1/12 doz.
        var converter = new FakeMealPlanUnitConverter((from, to) =>
            from == eaUnitId && to == dozUnitId ? 1m / 12m : null);

        var svc = BuildService(priceReader: fakePriceReader, catalogReader: catalogReader, unitConverter: converter);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Full, result.Completeness);
        // Compared to 2dp: the recurring-decimal 1/12 conversion factor (mirrors a real ea->doz
        // FactorToBase ratio) leaves a sub-cent rounding remainder from the decimal division.
        Assert.Equal(24.00m, result.Amount!.Value, 2); // (1.00 * 12) * 2 servings
    }

    // ── Product dish — unresolvable conversion returns partial, never fabricated (AC3) ──

    [Fact]
    public async Task RollUpMealAsync_ProductDish_ReturnsPartial_WhenPriceUnitCannotConvertToDefaultUnit()
    {
        var pricedRecipe = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var eaUnitId = Guid.NewGuid();
        var kgUnitId = Guid.NewGuid(); // mass default unit, priced in an unrelated count unit

        var recipeReader = new FakePriceEnrichmentReader(pricedRecipe, new RecipeDishEnrichment(100, 4m, false, false));
        var pricePoint = new MealPlanPricePoint(productId, 3m, 1m, eaUnitId, null);
        var fakePriceReader = new FakePriceReader(pricePoint);
        var catalogReader = new FakeMealPlanCatalogProductReader(new Dictionary<Guid, Guid> { [productId] = kgUnitId });
        // No path from ea to kg — no ProductConversion registered for this product.
        var converter = new FakeMealPlanUnitConverter((_, _) => null);

        var svc = BuildService(recipeReader: recipeReader, priceReader: fakePriceReader,
            catalogReader: catalogReader, unitConverter: converter);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [
            new DishSpec(DishKind.Recipe, pricedRecipe, 2),
            new DishSpec(DishKind.Product, productId, 2),
        ], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
        Assert.Equal(4m, result.Amount); // only the priced recipe contributes — never a fabricated number
    }

    // ── Product dish — no price data ──────────────────────────────────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsNone_WhenProductHasNoPriceData()
    {
        var svc = BuildService(priceReader: new FakePriceReader(null));
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, Guid.NewGuid(), 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(MealCost.None, result);
    }

    // ── Multi-dish — partial completeness when one dish unpriced ─────────────

    [Fact]
    public async Task RollUpMealAsync_ReturnsPartial_WhenOneDishIsUnpriced()
    {
        var pricedRecipe = Guid.NewGuid();
        var unpricedRecipe = Guid.NewGuid();
        var reader = new FakeMultiPriceEnrichmentReader([
            (pricedRecipe,   new RecipeDishEnrichment(100, 4m, false, false)),
            (unpricedRecipe, new RecipeDishEnrichment(100, null, false, false)),
        ]);

        var svc = BuildService(recipeReader: reader);
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [
            new DishSpec(DishKind.Recipe, pricedRecipe, 2),
            new DishSpec(DishKind.Recipe, unpricedRecipe, 2),
        ], null, "manual", UserId, Clock);

        var result = await svc.RollUpMealAsync(plan.PlannedMeals[0]);

        Assert.Equal(CostCompleteness.Partial, result.Completeness);
        Assert.Equal(4m, result.Amount); // only the priced one contributes
    }

    // ── Week roll-up — sums across meals ──────────────────────────────────────

    [Fact]
    public async Task RollUpWeekAsync_SumsAcrossMultipleMeals()
    {
        var recipeId = Guid.NewGuid();
        var enrichment = new RecipeDishEnrichment(100, 5m, false, false);
        var reader = new FakePriceEnrichmentReader(recipeId, enrichment);
        var svc = BuildService(recipeReader: reader);

        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(1), SlotA,
            [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);

        var result = await svc.RollUpWeekAsync(plan);

        Assert.Equal(10m, result.Amount); // 5 + 5
        Assert.Equal(CostCompleteness.Full, result.Completeness);
    }

    // ── Week roll-up — skips note meals ───────────────────────────────────────

    [Fact]
    public async Task RollUpWeekAsync_SkipsNoteMeals()
    {
        var svc = BuildService();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);

        var result = await svc.RollUpWeekAsync(plan);

        Assert.Equal(MealCost.None, result);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static PlanCostingService BuildService(
        IRecipeReadModel? recipeReader = null,
        IMealPlanPriceReader? priceReader = null,
        IMealPlanCatalogProductReader? catalogReader = null,
        IMealPlanUnitConverter? unitConverter = null)
        => new(
            recipeReader ?? new FakePriceEnrichmentReader(Guid.Empty, null),
            priceReader ?? new FakePriceReader(null),
            catalogReader ?? new FakeMealPlanCatalogProductReader(new Dictionary<Guid, Guid>()),
            unitConverter ?? new FakeMealPlanUnitConverter((_, _) => null));
}

// ── test doubles ──────────────────────────────────────────────────────────────

internal sealed class FakePriceEnrichmentReader(Guid recipeId, RecipeDishEnrichment? enrichment) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult(id == recipeId ? enrichment : null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class FakeMultiPriceEnrichmentReader(
    IReadOnlyList<(Guid RecipeId, RecipeDishEnrichment? Enrichment)> map) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
    {
        var match = map.FirstOrDefault(m => m.RecipeId == id);
        return Task.FromResult(match.Enrichment);
    }

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class FakePriceReader(MealPlanPricePoint? price) : IMealPlanPriceReader
{
    public Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(price);
}

/// <summary>Catalog reader returning a configured default unit id per product (plantry-9n7l);
/// a product absent from the map resolves as "unresolvable" (null), matching production behaviour
/// for an archived/unknown product.</summary>
internal sealed class FakeMealPlanCatalogProductReader(IReadOnlyDictionary<Guid, Guid> defaultUnitByProduct)
    : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<Guid?> FindDefaultUnitIdAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(defaultUnitByProduct.TryGetValue(productId, out var id) ? (Guid?)id : null);
}

/// <summary>Unit converter driven by a configurable (fromUnit, toUnit) -> factor lookup
/// (plantry-9n7l); returns a same-unit identity without consulting the lookup, and fails loudly
/// (mirroring the real port) when the lookup has no entry for a differing pair.</summary>
internal sealed class FakeMealPlanUnitConverter(Func<Guid, Guid, decimal?> lookup) : IMealPlanUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
    {
        if (fromUnitId == toUnitId)
            return Task.FromResult(Result<decimal>.Success(amount));

        var factor = lookup(fromUnitId, toUnitId);
        return Task.FromResult(factor.HasValue
            ? Result<decimal>.Success(amount * factor.Value)
            : Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No conversion path.")));
    }
}
