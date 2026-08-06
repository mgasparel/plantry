using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.MealPlanning.Domain;
using Plantry.Tests.Unit.Shopping.Application;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L2 unit tests for <see cref="ShopForWeekService"/>.
/// Uses in-memory fakes — no EF, no DB, no real adapters. Writes go through the real
/// <see cref="AddItemCommand"/> against a <see cref="FakeShoppingListRepository"/> (intra-context
/// since the Planning merge, ADR-024, plantry-g3da.5 — formerly through the mockable
/// IMealPlanShoppingWriter ACL port), so assertions read the resulting <see cref="ShoppingList"/>
/// aggregate state (items + per-source contributions) instead of captured writer calls.
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
        var (svc, _) = BuildService(repo: new FakeMealPlanRepository());

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

        var (svc, _) = BuildService(repo: repo);
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

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        var item = Assert.Single(list.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(2m, item.Quantity);
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
        plan.AssignMeal(Monday, SlotA, [DishSpec.ForProduct(productId, 3m, unitId)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var (svc, shopRepo) = BuildService(repo: repo, stockReader: fakeStock);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        Assert.Equal(2m, list.Items[0].Quantity); // 3 - 1 = 2
    }

    [Fact]
    public async Task ExecuteAsync_ConvertsStockAndWritesShortfallInSavedUnit()
    {
        var productId = Guid.NewGuid();
        var kgUnitId = Guid.NewGuid();
        var gramUnitId = Guid.NewGuid();
        var fakeStock = new FakeStockReaderForShop(
            new MealPlanProductStock(productId, 1m, kgUnitId, null));
        var converter = new FakeMealPlanUnitConverter((from, to) =>
            from == kgUnitId && to == gramUnitId ? 1000m : null);
        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [DishSpec.ForProduct(productId, 1500m, gramUnitId)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var (svc, shopRepo) = BuildService(repo: repo, stockReader: fakeStock, unitConverter: converter);
        await svc.ExecuteAsync(HouseholdId, Monday);

        var list = await GetListAsync(shopRepo);
        var item = Assert.Single(list.Items);
        Assert.Equal(500m, item.Quantity);
        Assert.Equal(gramUnitId, item.UnitId);
    }

    [Fact]
    public async Task ExecuteAsync_ConversionGapAbortsBeforeAnyShoppingWrite()
    {
        var recipeId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var kgUnitId = Guid.NewGuid();
        var gramUnitId = Guid.NewGuid();
        var reader = new FakeMissingIngredientsReader(
            recipeId, [new RecipeMissingIngredient(productId, 1m, kgUnitId)]);
        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [
            new DishSpec(DishKind.Recipe, recipeId, 2),
            DishSpec.ForProduct(productId, 500m, gramUnitId),
        ], null, "manual", UserId, Clock);
        repo.Stored = plan;
        var converter = new FakeMealPlanUnitConverter((_, _) => null);

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader, unitConverter: converter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExecuteAsync(HouseholdId, Monday));
        var list = await GetListAsync(shopRepo);
        Assert.Empty(list.Items);
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
        plan.AssignMeal(Monday, SlotA, [DishSpec.ForProduct(productId, 3m, unitId)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var (svc, shopRepo) = BuildService(repo: repo, stockReader: fakeStock);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        Assert.Equal(3m, list.Items[0].Quantity); // 3 needed - 0 available = 3
        Assert.Equal(unitId, list.Items[0].UnitId); // real unit, not Guid.Empty
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
        plan.AssignMeal(Monday, SlotA, [DishSpec.ForProduct(productId, 2m, unitId)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var (svc, shopRepo) = BuildService(repo: repo, stockReader: fakeStock);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(0, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        Assert.Empty(list.Items);
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

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        // One distinct product line (the sum happens in Shopping's contribution model, not here).
        Assert.Equal(1, result.ItemsAdded);

        var list = await GetListAsync(shopRepo);
        var item = Assert.Single(list.Items);
        Assert.Equal(productId, item.ProductId);

        // Two per-slot contributions on the ONE line, each stamped with its OWN planned_meal slot
        // id — never plan.Id.
        Assert.Equal(2, item.Contributions.Count);
        var slotIds = plan.PlannedMeals.Select(m => m.Id.Value).ToHashSet();
        Assert.All(item.Contributions, c => Assert.Equal(ItemSource.MealPlan, c.Source));
        Assert.All(item.Contributions, c => Assert.Contains(c.SourceRef!.Value, slotIds));
        Assert.DoesNotContain(plan.Id.Value, item.Contributions.Select(c => c.SourceRef));
        // Distinct slot ids — one contribution per slot, not collapsed.
        Assert.Equal(2, item.Contributions.Select(c => c.SourceRef).Distinct().Count());

        // Each slot contributed its own quantity for the shared product (1.5 and 0.5), same unit.
        Assert.Contains(item.Contributions, c => c.Quantity == 1.5m);
        Assert.Contains(item.Contributions, c => c.Quantity == 0.5m);
        Assert.All(item.Contributions, c => Assert.Equal(unitId, c.UnitId));
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

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader);

        await svc.ExecuteAsync(HouseholdId, Monday);

        var list = await GetListAsync(shopRepo);
        var item = Assert.Single(list.Items);
        var contribution = Assert.Single(item.Contributions);
        var slotId = Assert.Single(plan.PlannedMeals).Id.Value;
        Assert.Equal(slotId, contribution.SourceRef);
        Assert.NotEqual(plan.Id.Value, contribution.SourceRef);
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

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        // ONE item for the one occupied slot; the two dishes' needs are summed into one contribution.
        var item = Assert.Single(list.Items);
        var contribution = Assert.Single(item.Contributions);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(1.25m, contribution.Quantity); // 1 + 0.25 summed within the slot
    }

    // ── Cooked dish exclusion (plantry-366k) ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DoesNotAddMissingIngredients_ForCookedRecipeDish()
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

        var dishId = plan.PlannedMeals.Single().PlannedDishes.Single().Id.Value;
        var cookStatusReader = new FakeCookStatusReader(
            new Dictionary<Guid, DishCookStatus> { [dishId] = new(Clock.UtcNow) });

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader, cookStatusReader: cookStatusReader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(0, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotAddProduct_ForConsumedProductDish()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var fakeStock = new FakeStockReaderForShop(
            new MealPlanProductStock(productId, 1m, unitId, null));

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [DishSpec.ForProduct(productId, 3m, unitId)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var dishId = plan.PlannedMeals.Single().PlannedDishes.Single().Id.Value;
        var cookStatusReader = new FakeCookStatusReader(
            new Dictionary<Guid, DishCookStatus> { [dishId] = new(Clock.UtcNow) });

        var (svc, shopRepo) = BuildService(repo: repo, stockReader: fakeStock, cookStatusReader: cookStatusReader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(0, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task ExecuteAsync_AddsOnlyUncookedDish_WhenMealHasOneCookedAndOneUncookedDish()
    {
        var recipeId1 = Guid.NewGuid(); // cooked
        var recipeId2 = Guid.NewGuid(); // not cooked
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        var reader = new FakeMultiMissingReader([
            (recipeId1, [new RecipeMissingIngredient(productA, 1m, unitId)]),
            (recipeId2, [new RecipeMissingIngredient(productB, 1m, unitId)]),
        ]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId1, 2), new DishSpec(DishKind.Recipe, recipeId2, 2)],
            null, "manual", UserId, Clock);
        repo.Stored = plan;

        var dishes = plan.PlannedMeals.Single().PlannedDishes.ToList();
        var cookedDishId = dishes[0].RecipeId == recipeId1 ? dishes[0].Id.Value : dishes[1].Id.Value;
        var cookStatusReader = new FakeCookStatusReader(
            new Dictionary<Guid, DishCookStatus> { [cookedDishId] = new(Clock.UtcNow) });

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader, cookStatusReader: cookStatusReader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        var item = Assert.Single(list.Items);
        Assert.Equal(productB, item.ProductId);
        var contribution = Assert.Single(item.Contributions);
        Assert.Equal(plan.PlannedMeals.Single().Id.Value, contribution.SourceRef);
    }

    [Fact]
    public async Task ExecuteAsync_AddsNothing_WhenEveryDishInWeekIsCooked()
    {
        var recipeId1 = Guid.NewGuid();
        var recipeId2 = Guid.NewGuid();
        var productId1 = Guid.NewGuid();
        var productId2 = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        var reader = new FakeMultiMissingReader([
            (recipeId1, [new RecipeMissingIngredient(productId1, 1m, unitId)]),
            (recipeId2, [new RecipeMissingIngredient(productId2, 1m, unitId)]),
        ]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId1, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(3), SlotB,
            [new DishSpec(DishKind.Recipe, recipeId2, 2)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var allDishIds = plan.PlannedMeals.SelectMany(m => m.PlannedDishes).Select(d => d.Id.Value);
        var cookStatusReader = new FakeCookStatusReader(
            allDishIds.ToDictionary(id => id, _ => new DishCookStatus(Clock.UtcNow)));

        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader, cookStatusReader: cookStatusReader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(0, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task ExecuteAsync_BehavesIdenticallyToBaseline_WhenNothingIsCooked()
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

        var cookStatusReader = new FakeCookStatusReader(new Dictionary<Guid, DishCookStatus>());
        var (svc, shopRepo) = BuildService(repo: repo, recipeReader: reader, cookStatusReader: cookStatusReader);

        var result = await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, result.ItemsAdded);
        var list = await GetListAsync(shopRepo);
        var item = Assert.Single(list.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(2m, item.Quantity);
    }

    [Fact]
    public async Task ExecuteAsync_CallsCookStatusReaderExactlyOnce_ForMultiSlotWeek()
    {
        var recipeId1 = Guid.NewGuid();
        var recipeId2 = Guid.NewGuid();
        var productId1 = Guid.NewGuid();
        var productId2 = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        var reader = new FakeMultiMissingReader([
            (recipeId1, [new RecipeMissingIngredient(productId1, 1m, unitId)]),
            (recipeId2, [new RecipeMissingIngredient(productId2, 1m, unitId)]),
        ]);

        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA,
            [new DishSpec(DishKind.Recipe, recipeId1, 2)], null, "manual", UserId, Clock);
        plan.AssignMeal(Monday.AddDays(3), SlotB,
            [new DishSpec(DishKind.Recipe, recipeId2, 2)], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var cookStatusReader = new FakeCookStatusReader(new Dictionary<Guid, DishCookStatus>());
        var (svc, _) = BuildService(repo: repo, recipeReader: reader, cookStatusReader: cookStatusReader);

        await svc.ExecuteAsync(HouseholdId, Monday);

        Assert.Equal(1, cookStatusReader.CallCount);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<ShoppingList> GetListAsync(FakeShoppingListRepository shopRepo) =>
        (await shopRepo.GetForHouseholdAsync(HouseholdId))!;

    private static (ShopForWeekService Service, FakeShoppingListRepository ShopRepo) BuildService(
        FakeMealPlanRepository? repo = null,
        IRecipeReadModel? recipeReader = null,
        IMealPlanStockReader? stockReader = null,
        IMealPlanUnitConverter? unitConverter = null,
        FakeCookStatusReader? cookStatusReader = null)
    {
        var shopRepo = new FakeShoppingListRepository();
        shopRepo.Seed(ShoppingList.Create(HouseholdId, Clock));

        var svc = new ShopForWeekService(
            repo ?? new FakeMealPlanRepository(),
            recipeReader ?? new FakeMissingIngredientsReader(Guid.Empty, []),
            stockReader ?? new FakeStockReaderForShop(null),
            shopRepo,
            new FakeShoppingCatalogReader(),
            Clock,
            new FakeTenantContext(HouseholdId.Value),
            NullLogger<ShopForWeekService>.Instance,
            cookStatusReader ?? new FakeCookStatusReader(new Dictionary<Guid, DishCookStatus>()),
            unitConverter);

        return (svc, shopRepo);
    }
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

/// <summary>
/// Fixed <see cref="IMealPlanCookStatusReader"/> — returns exactly the pre-seeded statuses,
/// filtered to what was asked for, and counts calls so tests can assert the batching discipline
/// (one call per <see cref="ShopForWeekService.ExecuteAsync"/>, never per-dish).
/// </summary>
internal sealed class FakeCookStatusReader(IReadOnlyDictionary<Guid, DishCookStatus> statuses) : IMealPlanCookStatusReader
{
    public int CallCount { get; private set; }

    public Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        CallCount++;
        IReadOnlyDictionary<Guid, DishCookStatus> result = plannedDishIds
            .Where(statuses.ContainsKey)
            .ToDictionary(id => id, id => statuses[id]);
        return Task.FromResult(result);
    }
}
