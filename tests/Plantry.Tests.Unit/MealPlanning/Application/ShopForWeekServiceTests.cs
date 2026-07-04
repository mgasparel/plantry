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
/// Covers: no plan, note-meal skip, recipe-missing aggregation, product-dish short-stock,
///         full stock (nothing added), per-slot sourceRef stamping (plantry-jie7 — slot id not
///         plan.Id), same-product-across-two-slots (one line, per-slot contributions), and
///         same-product-within-one-slot summing.
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

    // ── Same product across two slots → ONE line, two per-slot contributions (plantry-jie7) ──

    [Fact]
    public async Task ExecuteAsync_SameProductAcrossTwoSlots_WritesOnePerSlotContributionEach_KeyedBySlotId()
    {
        var recipeId1 = Guid.NewGuid();
        var recipeId2 = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        // Two different recipes on two different slots, each missing the same product.
        var reader = new FakeMultiMissingReader([
            (recipeId1, [new RecipeMissingIngredient(productId, 1.5m, unitId)]),
            (recipeId2, [new RecipeMissingIngredient(productId, 0.5m, unitId)]),
        ]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId1, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(3), SlotB,
            [new DishSpec(DishKind.Recipe, recipeId2, 2)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, recipeReader: reader, writer: writer);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        // One distinct product line (the sum happens in Shopping's contribution model, not here).
        Assert.Equal(1, result.ItemsAdded);

        // Two per-slot writer calls, each stamped with its OWN planned_meal slot id — never plan.Id.
        Assert.Equal(2, writer.Calls.Count);
        var slotIds = plan.PlannedMeals.Select(m => m.Id.Value).ToHashSet();
        Assert.All(writer.Calls, c => Assert.Equal("meal_plan", c.Source));
        Assert.All(writer.Calls, c => Assert.Contains(c.SourceRef, slotIds));
        Assert.DoesNotContain(plan.Id.Value, writer.Calls.Select(c => c.SourceRef));
        // Distinct slot ids — one contribution per slot, not collapsed.
        Assert.Equal(2, writer.Calls.Select(c => c.SourceRef).Distinct().Count());

        // Each slot contributed its own quantity for the shared product (1.5 and 0.5), same unit.
        var perProduct = writer.WrittenItems.Where(i => i.ProductId == productId).ToList();
        Assert.Equal(2, perProduct.Count);
        Assert.Contains(perProduct, i => i.Quantity == 1.5m);
        Assert.Contains(perProduct, i => i.Quantity == 0.5m);
        Assert.All(perProduct, i => Assert.Equal(unitId, i.UnitId));
    }

    // ── Single slot stamps that slot's id as sourceRef (not plan.Id) (plantry-jie7) ──

    [Fact]
    public async Task ExecuteAsync_StampsPlannedMealSlotId_NotPlanId()
    {
        var recipeId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        var reader = new FakeMissingIngredientsReader(
            recipeId, [new RecipeMissingIngredient(productId, 2m, unitId)]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [new DishSpec(DishKind.Recipe, recipeId, 2)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, recipeReader: reader, writer: writer);

        await svc.ExecuteAsync(HouseholdId, Monday);

        var call = Assert.Single(writer.Calls);
        var slotId = Assert.Single(plan.PlannedMeals).Id.Value;
        Assert.Equal(slotId, call.SourceRef);
        Assert.NotEqual(plan.Id.Value, call.SourceRef);
    }

    // ── Same product needed by two dishes in ONE slot → summed into that slot's single item ──

    [Fact]
    public async Task ExecuteAsync_SumsSameProductWithinOneSlot_IntoSingleSlotItem()
    {
        var recipeId1 = Guid.NewGuid();
        var recipeId2 = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        // Two recipe dishes in the SAME slot, both missing the same product.
        var reader = new FakeMultiMissingReader([
            (recipeId1, [new RecipeMissingIngredient(productId, 1m, unitId)]),
            (recipeId2, [new RecipeMissingIngredient(productId, 0.25m, unitId)]),
        ]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId1, 2), new DishSpec(DishKind.Recipe, recipeId2, 2)],
            null, "manual", UserId, Clock);
        repo.Stored = plan;

        var writer = new FakeShoppingWriter();
        var svc = BuildService(repo: repo, recipeReader: reader, writer: writer);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        // ONE call for the one occupied slot; the two dishes' needs are summed into one item.
        var call = Assert.Single(writer.Calls);
        var item = Assert.Single(call.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(1.25m, item.Quantity); // 1 + 0.25 summed within the slot
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
    /// <summary>Every item written across all calls, in call order (contributions, not merged lines).</summary>
    public List<MealPlanShoppingItem> WrittenItems { get; } = [];

    /// <summary>One entry per AddItemsAsync call — records the per-slot sourceRef and its items.</summary>
    public List<(string Source, Guid SourceRef, IReadOnlyList<MealPlanShoppingItem> Items)> Calls { get; } = [];

    public string? LastSource { get; private set; }
    public Guid LastSourceRef { get; private set; }

    public Task AddItemsAsync(
        IEnumerable<MealPlanShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default)
    {
        var materialized = items.ToList();
        WrittenItems.AddRange(materialized);
        Calls.Add((source, sourceRef, materialized));
        LastSource = source;
        LastSourceRef = sourceRef;
        return Task.CompletedTask;
    }
}
