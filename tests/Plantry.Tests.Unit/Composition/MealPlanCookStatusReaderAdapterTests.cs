using Plantry.Inventory.Application;
using Plantry.Planning.Application;
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
        var eachUnitId = Guid.NewGuid();
        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[dishId] = [new JournalMovement(-4m, eatenAt, eachUnitId)];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(eatenAt, status.At);
    }

    [Fact]
    public async Task Product_dish_fully_undone_nets_to_pending_ie_absent()
    {
        var dishId = Guid.NewGuid();
        var eachUnitId = Guid.NewGuid();
        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[dishId] =
        [
            new JournalMovement(-4m, DateTimeOffset.UtcNow.AddMinutes(-10), eachUnitId),
            new JournalMovement(4m, DateTimeOffset.UtcNow.AddMinutes(-8), eachUnitId), // compensating undo ADD
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
        var eachUnitId = Guid.NewGuid();
        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[dishId] =
        [
            new JournalMovement(-4m, firstEat, eachUnitId),
            new JournalMovement(4m, undo, eachUnitId),
            new JournalMovement(-2.5m, reEat, eachUnitId),
        ];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(reEat, status.At);
        // plantry-vqa7: re-eat after undo reports the LATEST eat's net (-2.5, the second eat alone —
        // the undo cancelled the first eat's -4 out of the running net), not the sum across both eats.
        // The re-eat's quantity (-2.5) is deliberately different from the first eat's (-4) so this
        // assertion can only pass if the adapter is genuinely reading the post-undo net, not just
        // echoing back whichever fixed magnitude every movement in the fixture happened to share.
        Assert.Equal(2.5m, status.ConsumedQuantity);
        Assert.Equal(eachUnitId, status.ConsumedUnitId);
    }

    [Fact]
    public async Task Mixed_recipe_and_product_dishes_resolve_independently_in_one_batch()
    {
        var recipeDishId = Guid.NewGuid();
        var productDishId = Guid.NewGuid();
        var pendingRecipeDishId = Guid.NewGuid();
        var cookedAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        var eatenAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var eachUnitId = Guid.NewGuid();

        var cookEvents = new TestCookEventRepository();
        cookEvents.CookedAtByPlannedDishId[recipeDishId] = cookedAt;

        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[productDishId] = [new JournalMovement(-2m, eatenAt, eachUnitId)];

        var statuses = await Adapter(cookEvents, journal)
            .GetStatusesAsync([recipeDishId, productDishId, pendingRecipeDishId]);

        Assert.Equal(2, statuses.Count);
        Assert.Equal(cookedAt, statuses[recipeDishId].At);
        Assert.Null(statuses[recipeDishId].ConsumedQuantity); // recipe dishes never set Consumed*
        Assert.Equal(eatenAt, statuses[productDishId].At);
        Assert.False(statuses.ContainsKey(pendingRecipeDishId));
    }

    // ── plantry-vqa7: actual-eaten quantity display ─────────────────────────────────────────────────

    [Fact]
    public async Task Product_dish_with_uniform_unit_movements_resolves_ConsumedQuantity_to_negated_net()
    {
        var dishId = Guid.NewGuid();
        var eachUnitId = Guid.NewGuid();
        var journal = new TestJournalReader();
        // An adjusted eat: planned 2, but 2.1 actually consumed in one movement.
        journal.MovementsBySourceRef[dishId] = [new JournalMovement(-2.1m, DateTimeOffset.UtcNow, eachUnitId)];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(2.1m, status.ConsumedQuantity);
        Assert.Equal(eachUnitId, status.ConsumedUnitId);
    }

    [Fact]
    public async Task Product_dish_with_multiple_uniform_unit_movements_sums_them()
    {
        // ProductStock.Consume writes one negative journal row PER LOT touched (MealPlanEatWriterAdapter
        // doc comment) — a FEFO eat spanning two same-unit lots is multi-row uniform-unit, the real,
        // common case where "sum every movement" and "read the latest movement's own delta" diverge.
        var dishId = Guid.NewGuid();
        var eachUnitId = Guid.NewGuid();
        var t1 = DateTimeOffset.UtcNow.AddSeconds(-2);
        var t2 = DateTimeOffset.UtcNow;
        var journal = new TestJournalReader();
        journal.MovementsBySourceRef[dishId] =
        [
            new JournalMovement(-2m, t1, eachUnitId),
            new JournalMovement(-0.5m, t2, eachUnitId),
        ];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Equal(2.5m, status.ConsumedQuantity);
        Assert.Equal(eachUnitId, status.ConsumedUnitId);
    }

    [Fact]
    public async Task Product_dish_with_mixed_unit_movements_leaves_ConsumedQuantity_and_ConsumedUnitId_null()
    {
        var dishId = Guid.NewGuid();
        var gramsUnitId = Guid.NewGuid();
        var eachUnitId = Guid.NewGuid();
        var journal = new TestJournalReader();
        // A shortfall eat that drew from two lots in different units — net is still negative (done),
        // but summing raw Delta across units is not a displayable magnitude (plantry-wiv2).
        journal.MovementsBySourceRef[dishId] =
        [
            new JournalMovement(-1.5m, DateTimeOffset.UtcNow.AddMinutes(-2), gramsUnitId),
            new JournalMovement(-1m, DateTimeOffset.UtcNow, eachUnitId),
        ];

        var statuses = await Adapter(new TestCookEventRepository(), journal).GetStatusesAsync([dishId]);

        var status = Assert.Single(statuses).Value;
        Assert.Null(status.ConsumedQuantity);
        Assert.Null(status.ConsumedUnitId);
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
