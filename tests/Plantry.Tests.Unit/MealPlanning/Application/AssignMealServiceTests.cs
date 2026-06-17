using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Application;

/// <summary>
/// L2 unit tests for <see cref="AssignMealService"/> and <see cref="MoveMealService"/>
/// using in-memory fakes (no EF, no DB).
/// </summary>
public sealed class AssignMealServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();

    private static AssignMealService BuildService(
        FakeMealPlanRepository? planRepo = null,
        FakeSlotConfigRepository? slotRepo = null,
        FakePrefsRepo? prefsRepo = null,
        FakeRecipeReadModel? recipeReader = null,
        FakeCatalogProductReader? catalogReader = null)
    {
        return new AssignMealService(
            planRepo ?? new FakeMealPlanRepository(),
            slotRepo ?? new FakeSlotConfigRepository(),
            prefsRepo ?? new FakePrefsRepo(),
            recipeReader ?? new FakeRecipeReadModel(),
            catalogReader ?? new FakeCatalogProductReader(),
            new MealConstraintResolver(),
            Clock);
    }

    private static DishSpec RecipeDish() => new(DishKind.Recipe, Guid.NewGuid(), 2);

    // ── AssignDishesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task AssignDishesAsync_CreatesNewPlan_AndSaves()
    {
        var repo = new FakeMealPlanRepository();
        var svc = BuildService(planRepo: repo);

        await svc.AssignDishesAsync(HouseholdId, Monday, SlotA, [RecipeDish()], null, UserId);

        Assert.NotNull(repo.Stored);
        Assert.Single(repo.Stored!.PlannedMeals);
        Assert.Equal(1, repo.SavedCount);
    }

    [Fact]
    public async Task AssignDishesAsync_RejectsUnknownProduct()
    {
        var catalogReader = new FakeCatalogProductReader(existsResult: false);
        var svc = BuildService(catalogReader: catalogReader);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AssignDishesAsync(HouseholdId, Monday, SlotA,
                [new DishSpec(DishKind.Product, Guid.NewGuid(), 1)],
                null, UserId));
    }

    [Fact]
    public async Task AssignDishesAsync_AcceptsKnownProduct()
    {
        var catalogReader = new FakeCatalogProductReader(existsResult: true);
        var repo = new FakeMealPlanRepository();
        var svc = BuildService(planRepo: repo, catalogReader: catalogReader);

        await svc.AssignDishesAsync(HouseholdId, Monday, SlotA,
            [new DishSpec(DishKind.Product, Guid.NewGuid(), 1)],
            null, UserId);

        Assert.NotNull(repo.Stored);
        Assert.Single(repo.Stored!.PlannedMeals);
    }

    [Fact]
    public async Task AssignDishesAsync_SourceIsManual()
    {
        var repo = new FakeMealPlanRepository();
        var svc = BuildService(planRepo: repo);

        await svc.AssignDishesAsync(HouseholdId, Monday, SlotA, [RecipeDish()], null, UserId);

        Assert.Equal("manual", repo.Stored!.PlannedMeals[0].Source);
    }

    // ── AssignNoteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task AssignNoteAsync_CreatesNoteInPlan_AndSaves()
    {
        var repo = new FakeMealPlanRepository();
        var svc = BuildService(planRepo: repo);

        await svc.AssignNoteAsync(HouseholdId, Monday, SlotA, "Takeout", null, UserId);

        var meal = Assert.Single(repo.Stored!.PlannedMeals);
        Assert.Equal("Takeout", meal.Note);
        Assert.Equal(1, repo.SavedCount);
    }

    // ── ClearMealAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearMealAsync_IsNoOp_WhenNoPlanExists()
    {
        var repo = new FakeMealPlanRepository();
        var svc = BuildService(planRepo: repo);

        // Should not throw
        await svc.ClearMealAsync(HouseholdId, Monday, SlotA);
    }

    [Fact]
    public async Task ClearMealAsync_RemovesMeal_WhenPresent()
    {
        var repo = new FakeMealPlanRepository();
        var svc = BuildService(planRepo: repo);

        await svc.AssignDishesAsync(HouseholdId, Monday, SlotA, [RecipeDish()], null, UserId);
        Assert.Single(repo.Stored!.PlannedMeals);

        await svc.ClearMealAsync(HouseholdId, Monday, SlotA);

        Assert.Empty(repo.Stored!.PlannedMeals);
    }
}

public sealed class MoveMealServiceTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly MealSlotId SlotA = MealSlotId.New();
    private static readonly MealSlotId SlotB = MealSlotId.New();
    private static readonly Guid UserId = Guid.NewGuid();

    private static MoveMealService BuildService(FakeMealPlanRepository repo)
        => new(repo, Clock);

    private static DishSpec RecipeDish() => new(DishKind.Recipe, Guid.NewGuid(), 2);

    [Fact]
    public async Task MoveAsync_RelocatesMealToEmptyCell()
    {
        var repo = new FakeMealPlanRepository();
        // Seed a plan with a meal
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var tuesday = Monday.AddDays(1);
        var svc = BuildService(repo);
        await svc.MoveAsync(HouseholdId, Monday, SlotA, tuesday, SlotB);

        var moved = Assert.Single(plan.PlannedMeals);
        Assert.Equal(tuesday, moved.Date);
        Assert.Equal(SlotB, moved.MealSlotId);
        Assert.Equal(1, repo.SavedCount);
    }

    [Fact]
    public async Task MoveAsync_ThrowsForCrossWeekMove()
    {
        var repo = new FakeMealPlanRepository();
        var nextMonday = Monday.AddDays(7);
        var svc = BuildService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.MoveAsync(HouseholdId, Monday, SlotA, nextMonday, SlotB));
    }

    [Fact]
    public async Task MoveAsync_ThrowsWhenNoPlanExists()
    {
        var repo = new FakeMealPlanRepository(); // no stored plan
        var svc = BuildService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.MoveAsync(HouseholdId, Monday, SlotA, Monday.AddDays(1), SlotB));
    }

    [Fact]
    public async Task MoveAsync_SwapsViaSqlBypass_WhenTargetCellOccupied()
    {
        // Swap: both cells occupied → SwapMealPositionsAsync is called instead of SaveChangesAsync.
        var repo = new FakeMealPlanRepository();
        var plan = MealPlan.Start(HouseholdId, Monday, Clock);
        plan.AssignMeal(Monday, SlotA, [RecipeDish()], null, "manual", UserId, Clock);
        var tuesday = Monday.AddDays(1);
        plan.AssignMeal(tuesday, SlotB, [RecipeDish()], null, "manual", UserId, Clock);
        repo.Stored = plan;

        var svc = BuildService(repo);
        await svc.MoveAsync(HouseholdId, Monday, SlotA, tuesday, SlotB);

        // Swap route was used (not SaveChangesAsync)
        Assert.NotNull(repo.LastSwap);
        Assert.Equal(0, repo.SavedCount);
    }
}

// ── test doubles ──────────────────────────────────────────────────────────────

public sealed class FakeMealPlanRepository : IMealPlanRepository
{
    public MealPlan? Stored { get; set; }
    public int SavedCount { get; private set; }
    public (PlannedMealId MealAId, DateOnly NewDateA, MealSlotId NewSlotA, PlannedMealId MealBId, DateOnly NewDateB, MealSlotId NewSlotB)? LastSwap { get; private set; }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult(Stored);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
    {
        if (Stored is null)
        {
            Stored = MealPlan.Start(householdId, weekStart, clock);
        }
        return Task.FromResult(Stored);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SavedCount++;
        return Task.CompletedTask;
    }

    public Task SwapMealPositionsAsync(
        PlannedMealId mealAId, DateOnly newDateA, MealSlotId newSlotA,
        PlannedMealId mealBId, DateOnly newDateB, MealSlotId newSlotB,
        Guid updatedBy, DateTimeOffset now,
        CancellationToken ct = default)
    {
        LastSwap = (mealAId, newDateA, newSlotA, mealBId, newDateB, newSlotB);
        return Task.CompletedTask;
    }
}

public sealed class FakeSlotConfigRepository : IMealSlotConfigRepository
{
    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<MealSlotConfig?>(null);

    public Task AddAsync(MealSlotConfig config, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class FakePrefsRepo : IUserPreferenceRepository
{
    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<UserPreference?>(null);

    public Task AddAsync(UserPreference preference, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class FakeRecipeReadModel : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);
}

public sealed class FakeCatalogProductReader(bool existsResult = true) : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(existsResult);

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}
