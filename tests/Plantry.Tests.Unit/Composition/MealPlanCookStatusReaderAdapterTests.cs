using Plantry.Inventory.Application;
using Plantry.MealPlanning.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="MealPlanCookStatusReaderAdapter"/> (plantry-0eut) — the composition join that
/// derives per-planned-dish cooked/eaten state from Recipes CookEvents and Inventory journal movements,
/// without MealPlanning storing anything. Covers the mixed recipe+product dish case (acceptance criteria),
/// the eat/undo netting for product dishes, and the degrade-to-pending cases.
/// </summary>
public sealed class MealPlanCookStatusReaderAdapterTests
{
    private readonly Guid _householdId = Guid.NewGuid();

    private MealPlanCookStatusReaderAdapter Adapter(
        TestCookEventRepository cookEvents, TestJournalReader journal, Guid? household = null) =>
        new(cookEvents, journal, new TestTenantContext(household ?? _householdId));

    [Fact]
    public async Task Recipe_dish_with_a_matching_CookEvent_resolves_to_done_at_CookedAt()
    {
        var dishId = Guid.NewGuid();
        var cookedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var cookEvents = new TestCookEventRepository();
        cookEvents.CookedAtByPlannedDishId[dishId] = cookedAt;

        var statuses = await Adapter(cookEvents, new TestJournalReader()).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(cookedAt, status.At);
    }

    [Fact]
    public async Task Recipe_dish_with_no_matching_CookEvent_is_absent_ie_pending()
    {
        var dishId = Guid.NewGuid();
        var statuses = await Adapter(new TestCookEventRepository(), new TestJournalReader()).GetStatusesAsync([dishId]);

        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Product_dish_with_a_single_consuming_movement_resolves_to_done_at_that_time()
    {
        var dishId = Guid.NewGuid();
        var eatenAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[dishId] = [new JournalMovement(-4m, eatenAt)];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(eatenAt, status.At);
    }

    [Fact]
    public async Task Product_dish_fully_undone_nets_to_pending_ie_absent()
    {
        var dishId = Guid.NewGuid();
        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[dishId] =
        [
            new JournalMovement(-4m, DateTimeOffset.UtcNow.AddMinutes(-10)),
            new JournalMovement(4m, DateTimeOffset.UtcNow.AddMinutes(-8)), // compensating undo ADD
        ];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Product_dish_re_eaten_after_undo_resolves_to_the_latest_eat_time()
    {
        var dishId = Guid.NewGuid();
        var firstEat = DateTimeOffset.UtcNow.AddMinutes(-30);
        var undo = DateTimeOffset.UtcNow.AddMinutes(-20);
        var reEat = DateTimeOffset.UtcNow.AddMinutes(-5);
        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[dishId] =
        [
            new JournalMovement(-4m, firstEat),
            new JournalMovement(4m, undo),
            new JournalMovement(-4m, reEat),
        ];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(reEat, status.At);
    }

    [Fact]
    public async Task Mixed_recipe_and_product_dishes_resolve_independently_in_one_batch()
    {
        var recipeDishId = Guid.NewGuid();
        var productDishId = Guid.NewGuid();
        var pendingRecipeDishId = Guid.NewGuid();
        var cookedAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        var eatenAt = DateTimeOffset.UtcNow.AddMinutes(-3);

        var cookEvents = new TestCookEventRepository();
        cookEvents.CookedAtByPlannedDishId[recipeDishId] = cookedAt;

        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[productDishId] = [new JournalMovement(-2m, eatenAt)];

        var statuses = await Adapter(cookEvents, journal)
            .GetStatusesAsync([recipeDishId, productDishId, pendingRecipeDishId]);

        Assert.Equal(2, statuses.Count);
        Assert.Equal(cookedAt, statuses[recipeDishId].At);
        Assert.Equal(eatenAt, statuses[productDishId].At);
        Assert.False(statuses.ContainsKey(pendingRecipeDishId));
    }

    [Fact]
    public async Task Empty_input_returns_empty_without_any_household()
    {
        var statuses = await Adapter(new TestCookEventRepository(), new TestJournalReader(), household: null)
            .GetStatusesAsync([]);

        Assert.Empty(statuses);
    }

    [Fact]
    public async Task No_household_in_tenant_context_returns_empty_even_with_dish_ids()
    {
        var dishId = Guid.NewGuid();
        var cookEvents = new TestCookEventRepository();
        cookEvents.CookedAtByPlannedDishId[dishId] = DateTimeOffset.UtcNow;

        // Constructed directly (not via the Adapter helper) — the helper's `household ?? _householdId`
        // fallback exists so callers can OMIT the parameter for the common case, which means it cannot
        // also express "explicitly no household"; this is the one test that needs a real null.
        var adapter = new MealPlanCookStatusReaderAdapter(cookEvents, new TestJournalReader(), new TestTenantContext(null));

        var statuses = await adapter.GetStatusesAsync([dishId]);

        Assert.Empty(statuses);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────────

    private sealed class TestTenantContext(Guid? householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    private sealed class TestCookEventRepository : ICookEventRepository
    {
        public Dictionary<Guid, DateTimeOffset> CookedAtByPlannedDishId { get; } = [];

        public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLatestCookedAtByPlannedDishIdsAsync(
            IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, DateTimeOffset> result = plannedDishIds
                .Where(CookedAtByPlannedDishId.ContainsKey)
                .ToDictionary(id => id, id => CookedAtByPlannedDishId[id]);
            return Task.FromResult(result);
        }

        public Task AddAsync(CookEvent cookEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CookEvent>>([]);
        public Task<IReadOnlyList<CookEvent>> ListWithPendingLinesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CookEvent>>([]);
        public Task<IReadOnlyList<CookEvent>> ListWithDeferredUnitGapLinesForProductsAsync(
            IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CookEvent>>([]);
        public Task<IReadOnlyDictionary<Guid, RecipeId>> GetRecipeIdsByCookEventIdsAsync(
            IReadOnlyCollection<Guid> cookEventIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, RecipeId>>(new Dictionary<Guid, RecipeId>());
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestJournalReader : IJournalEntriesBySourceRefReader
    {
        public Dictionary<Guid, IReadOnlyList<JournalMovement>> MovementsBySourceRef { get; } = [];

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<JournalMovement>>> ListBySourceRefsAsync(
            IReadOnlyCollection<Guid> sourceRefs, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, IReadOnlyList<JournalMovement>> result = sourceRefs
                .Where(MovementsBySourceRef.ContainsKey)
                .ToDictionary(id => id, id => MovementsBySourceRef[id]);
            return Task.FromResult(result);
        }
    }
}
