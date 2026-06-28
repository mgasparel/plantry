using Microsoft.Extensions.Logging.Abstractions;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L2 unit tests for <see cref="ShopForWeekService"/>.
/// Uses in-memory fakes — no EF, no DB, no real adapters.
/// Covers: no plan, note-meal skip, recipe-missing aggregation,
///         product-dish short-stock, per-product sum, full stock (nothing added).
/// </summary>
public sealed class ShopForWeekServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 9);
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly MealSlotId SlotB = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();

    // ── No plan → 0 items ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Returns0_WhenNoPlanExists()
    {
        var svc = BuildService(repo: new FakeMealPlanRepository());

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(0, result.ItemsAdded);
    }

    // ── Note meal skipped ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SkipsNoteMeals()
    {
        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignNote(Monday, SlotA, "Takeout", null, "manual", UserId, Clock);
        repo.Stored = plan;

        var svc = BuildService(repo: repo);
        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(0, result.ItemsAdded);
    }

    // ── Recipe dish — missing ingredient added ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AddsMissingRecipeIngredients()
    {
        var recipeId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        var reader = new FakeMissingIngredientsReader(
            recipeId,
            [new RecipeMissingIngredient(productId, 2m, unitId)]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, recipeReader: reader, writer: writer);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        Assert.Single(writer.WrittenItems);
        Assert.Equal(productId, writer.WrittenItems[0].ProductId);
        Assert.Equal(2m, writer.WrittenItems[0].Quantity);
    }

    // ── Product dish — short stock adds deficit ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AddsProductDishDeficit_WhenShortOnStock()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        // 1 in stock, need 3 → deficit = 2
        var fakeStock = new FakeStockReaderForShop(
            new MealPlanProductStock(productId, 1m, unitId, null));

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 3)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, stockReader: fakeStock, writer: writer);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        Assert.Equal(2m, writer.WrittenItems[0].Quantity); // 3 - 1 = 2
    }

    // ── Product dish — never stocked (zero-qty snapshot with real unit) adds full servings ─

    [Fact]
    public async Task ExecuteAsync_AddsProductDish_WhenNeverStocked_ZeroQtySnapshot()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        // Zero-qty snapshot returned by the fixed MealPlanStockReaderAdapter
        // (was previously null, which caused Guid.Empty guard to drop the item)
        var zeroStock = new MealPlanProductStock(productId, 0m, unitId, null);
        var fakeStock = new FakeStockReaderForShop(zeroStock);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 3)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, stockReader: fakeStock, writer: writer);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        Assert.Equal(3m, writer.WrittenItems[0].Quantity); // 3 needed - 0 available = 3
        Assert.Equal(unitId, writer.WrittenItems[0].UnitId); // real unit, not Guid.Empty
    }

    // ── Product dish — fully stocked, nothing added ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_DoesNotAddProductDish_WhenFullyStocked()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        // 10 in stock, need 2 → no deficit
        var fakeStock = new FakeStockReaderForShop(
            new MealPlanProductStock(productId, 10m, unitId, null));

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Product, productId, 2)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, stockReader: fakeStock, writer: writer);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(0, result.ItemsAdded);
        Assert.Empty(writer.WrittenItems);
    }

    // ── Per-product quantity summed across meals ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SumsQuantityForSameProductAcrossMultipleMeals()
    {
        var recipeId1 = Guid.NewGuid();
        var recipeId2 = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        // Two different recipes, each missing the same product
        var reader = new FakeMultiMissingReader([
            (recipeId1, [new RecipeMissingIngredient(productId, 1.5m, unitId)]),
            (recipeId2, [new RecipeMissingIngredient(productId, 0.5m, unitId)]),
        ]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId1, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(1), SlotB,
            [new DishSpec(DishKind.Recipe, recipeId2, 2)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, recipeReader: reader, writer: writer);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded); // one product, merged
        Assert.Equal(2m, writer.WrittenItems[0].Quantity); // 1.5 + 0.5
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ShopForWeekService BuildService(
        FakeMealPlanRepository? repo = null,
        IRecipeReadModel? recipeReader = null,
        IMealPlanStockReader? stockReader = null,
        IMealPlanShoppingWriter? writer = null)
        => new(
            repo ?? new FakeMealPlanRepository(),
            recipeReader ?? new FakeMissingIngredientsReader(Guid.Empty, []),
            stockReader ?? new FakeStockReaderForShop(null),
            writer ?? new FakeShoppingWriter(),
            NullLogger<ShopForWeekService>.Instance);
}

// ── test doubles ──────────────────────────────────────────────────────────────

internal sealed class FakeMissingIngredientsReader(
    Guid recipeId,
    IReadOnlyList<RecipeMissingIngredient> missing) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult(id == recipeId ? missing : (IReadOnlyList<RecipeMissingIngredient>)[]);

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class FakeMultiMissingReader(
    IReadOnlyList<(Guid RecipeId, IReadOnlyList<RecipeMissingIngredient> Missing)> map) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
    {
        var match = map.FirstOrDefault(m => m.RecipeId == id);
        return Task.FromResult(match.Missing ?? (IReadOnlyList<RecipeMissingIngredient>)[]);
    }

    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class FakeStockReaderForShop(MealPlanProductStock? stock) : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(stock);
}

internal sealed class FakeShoppingWriter : IMealPlanShoppingWriter
{
    public List<MealPlanShoppingItem> WrittenItems { get; } = [];
    public string? LastSource { get; private set; }
    public Guid LastSourceRef { get; private set; }

    public Task AddItemsAsync(
        IEnumerable<MealPlanShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default)
    {
        WrittenItems.AddRange(items);
        LastSource = source;
        LastSourceRef = sourceRef;
        return Task.CompletedTask;
    }
}
