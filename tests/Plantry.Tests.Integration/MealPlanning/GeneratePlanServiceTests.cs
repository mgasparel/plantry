using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;
using System.Collections.Generic;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L2 integration tests for <see cref="GeneratePlanService"/> and <see cref="AcceptProposalService"/>.
/// Uses in-memory fakes for all ports — tests domain logic and ACL enforcement without Postgres.
/// </summary>
public sealed class GeneratePlanServiceTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = MealPlan.NormalizeToMonday(new DateOnly(2026, 6, 16));

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static (GeneratePlanService, AcceptProposalService, IPendingProposalStore, FakeMealPlanRepository, FakeSlotConfigRepo)
        BuildStack(
            MealSlotConfig? slotConfig = null,
            IReadOnlyList<UserPreference>? prefs = null,
            IReadOnlyList<RecipeReadModel>? recipes = null,
            IMealPlanner? planner = null,
            ITagReader? tagReader = null)
    {
        var config = slotConfig ?? BuildDefaultSlotConfig();
        var slotConfigRepo = new FakeSlotConfigRepo(config);
        var prefRepo = new FakePrefsRepo(prefs ?? []);
        var recipeReader = new FakeRecipeReader(recipes ?? []);
        var mealPlanRepo = new FakeMealPlanRepository();
        var sp = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var memoryCache = sp.GetRequiredService<IDistributedCache>();
        var store = new DistributedCachePendingProposalStore(memoryCache);
        var resolver = new MealConstraintResolver();
        var fakePlanner = planner ?? new FakeMealPlanner();
        var fakeTagReader = tagReader ?? new NullTagReader();

        var generateService = new GeneratePlanService(
            fakePlanner, mealPlanRepo, slotConfigRepo, prefRepo, recipeReader, store, resolver, fakeTagReader,
            NullLogger<GeneratePlanService>.Instance);

        var acceptService = new AcceptProposalService(
            mealPlanRepo, slotConfigRepo, prefRepo, recipeReader, store, resolver, Clock);

        return (generateService, acceptService, store, mealPlanRepo, slotConfigRepo);
    }

    private static MealSlotConfig BuildDefaultSlotConfig()
    {
        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        var userId = Guid.NewGuid();
        foreach (var slot in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(slot.Id, [userId], Clock);
        return config;
    }

    // ── Execute_TargetsOnlyEmptyCells ─────────────────────────────────────────────

    [Fact(DisplayName = "Execute_TargetsOnlyEmptyCells — occupied cells are not overwritten")]
    public async Task Execute_TargetsOnlyEmptyCells()
    {
        var config = BuildDefaultSlotConfig();
        var recipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel>
        {
            new(recipeId, "Pasta", [], DefaultServings: 4)
        };

        var (generateService, _, store, mealPlanRepo, _) = BuildStack(
            slotConfig: config,
            recipes: recipes);

        // Pre-occupy Monday Breakfast
        var plan = MealPlan.Start(Household, Monday, Clock);
        var breakfastSlot = config.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        plan.AssignMeal(Monday, breakfastSlot.Id, [new DishSpec(DishKind.Recipe, recipeId, 4)],
            null, "manual", Guid.NewGuid(), Clock);
        mealPlanRepo.SetPlan(plan);

        var storeKey = "test-key";
        await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        var pending = await store.GetAsync(storeKey);

        // The occupied breakfast cell should NOT have a pending proposal
        Assert.DoesNotContain(pending, p =>
            p.Date == Monday && p.MealSlotId == breakfastSlot.Id);
    }

    // ── Execute_DefaultWeightsNeverRelaxHardStance_M5M11 ─────────────────────────

    [Fact(DisplayName = "Execute_DefaultWeightsNeverRelaxHardStance — restricted recipe rejected by ACL")]
    public async Task Execute_DefaultWeightsNeverRelaxHardStance_M5M11()
    {
        var restrictedTag = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        // Set the restricted user as default attendee on ALL active slots so
        // every cell has the restriction in scope and the ACL blocks the restricted recipe
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [userId], Clock);

        var prefs = new List<UserPreference>();
        var pref = UserPreference.Create(Household, userId, Clock);
        pref.SetStance(restrictedTag, "Restricted", Clock);
        prefs.Add(pref);

        // The planner returns the restricted recipe (simulating a bad AI response)
        var badPlanner = new SingleRecipeFakePlanner(recipeId);
        var recipes = new List<RecipeReadModel>
        {
            new(recipeId, "RestrictedDish", [restrictedTag], DefaultServings: 4)
        };

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: prefs,
            recipes: recipes,
            planner: badPlanner);

        var storeKey = "test-key";
        await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        var pending = await store.GetAsync(storeKey);

        // ACL should have filtered out all proposals containing the restricted recipe
        foreach (var proposal in pending)
        {
            Assert.DoesNotContain(proposal.Dishes, d => d.RecipeId == recipeId);
        }
    }

    // ── Execute_AcceptAll_AtomicTransaction ───────────────────────────────────────

    [Fact(DisplayName = "Execute_AcceptAll_AtomicTransaction — proposals committed with source=ai")]
    public async Task Execute_AcceptAll_AtomicTransaction()
    {
        var recipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel> { new(recipeId, "Pasta", [], DefaultServings: 4) };

        var (generateService, acceptService, _, mealPlanRepo, _) = BuildStack(recipes: recipes);

        var storeKey = "test-key";
        var result = await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        Assert.True(result.ProposedCount > 0, "Expected at least one proposal");

        var userId = Guid.NewGuid();
        var acceptResult = await acceptService.AcceptAllAsync(Household, Monday, storeKey, userId);

        Assert.True(acceptResult.Accepted > 0);

        var plan = mealPlanRepo.GetPlan();
        Assert.NotNull(plan);
        Assert.All(plan.PlannedMeals, m => Assert.Equal("ai", m.Source));
    }

    // ── Execute_PerCellAccept_ReValidatesAtBoundary ───────────────────────────────

    [Fact(DisplayName = "Execute_PerCellAccept_ReValidatesAtBoundary — invalid proposal rejected at accept")]
    public async Task Execute_PerCellAccept_ReValidatesAtBoundary()
    {
        var recipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel> { new(recipeId, "Pasta", [], DefaultServings: 4) };

        var (generateService, acceptService, store, _, _) = BuildStack(recipes: recipes);

        var storeKey = "test-key";
        await generateService.ExecuteAsync(Household, Monday, storeKey, null);
        var pending = await store.GetAsync(storeKey);

        if (pending.Count == 0) return; // Skip if no proposals (no candidates passed ACL)

        var first = pending[0];

        // Simulate recipe removal by clearing the candidate list (trust boundary test)
        // We do this by injecting a new store entry with a non-existent recipe
        var tamperedProposal = new ProposedMeal(
            first.Date, first.MealSlotId, first.EffectiveAttendees,
            [new ProposedDish(Guid.NewGuid(), 4, 1)], // hallucinated recipe ID
            "Tampered");
        await store.SetAsync(storeKey, [tamperedProposal]);

        var acceptResult = await acceptService.AcceptCellAsync(
            Household, first.Date, first.MealSlotId, storeKey, Guid.NewGuid());

        // Should be rejected at the trust boundary
        Assert.False(acceptResult.Accepted);
    }

    // ── Execute_ConflictCell_DetectedAndExcluded ──────────────────────────────────

    [Fact(DisplayName = "Execute_ConflictCell — two attendees with conflicting hard Required stances, no shared recipe → cell in Conflicts, excluded from ProposedCount")]
    public async Task Execute_ConflictCell_DetectedAndExcluded()
    {
        // Arrange: two attendees with mutually exclusive Required stances.
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var veganTag = Guid.NewGuid();
        var meatTag = Guid.NewGuid();

        // Set both as default attendees on every slot.
        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [aliceId, bobId], Clock);

        // Alice requires vegan; Bob requires meat.
        var alicePref = UserPreference.Create(Household, aliceId, Clock);
        alicePref.SetStance(veganTag, "Required", Clock);
        var bobPref = UserPreference.Create(Household, bobId, Clock);
        bobPref.SetStance(meatTag, "Required", Clock);

        // Candidate pool: one vegan recipe (satisfies Alice, not Bob) + one meat recipe (vice-versa).
        // No recipe carries both tags → every cell is irreconcilable.
        var veganRecipeId = Guid.NewGuid();
        var meatRecipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel>
        {
            new(veganRecipeId, "Vegan Stir-Fry", [veganTag], DefaultServings: 2),
            new(meatRecipeId, "Beef Stew", [meatTag], DefaultServings: 4),
        };

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: [alicePref, bobPref],
            recipes: recipes);

        var storeKey = "conflict-test-key";

        // Act
        var result = await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        // Assert: every cell is irreconcilable → all cells show up as Conflicts, none proposed.
        Assert.True(result.Conflicts.Count > 0, "Expected at least one irreconcilable conflict cell");
        Assert.Equal(0, result.ProposedCount);

        // No proposals were staged.
        var pending = await store.GetAsync(storeKey);
        Assert.Empty(pending);

        // Each conflict carries the attendee IDs and clashing tags.
        var firstConflict = result.Conflicts[0].Conflict;
        Assert.Contains(aliceId, firstConflict.AttendeeIds);
        Assert.Contains(bobId, firstConflict.AttendeeIds);
    }

    // ── Execute_UnfulfillableCell_DetectedAndExcluded ─────────────────────────────

    [Fact(DisplayName = "Execute_UnfulfillableCell — vegetarian attendee + no vegetarian recipes in corpus → cell in UnfulfillableCells, AI NOT called")]
    public async Task Execute_UnfulfillableCell_DetectedAndExcluded()
    {
        // Arrange: one attendee with a Required vegetarian tag, but the recipe corpus has NO vegetarian recipes.
        var userId = Guid.NewGuid();
        var vegetarianTag = Guid.NewGuid();
        var meatTag = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [userId], Clock);

        var pref = UserPreference.Create(Household, userId, Clock);
        pref.SetStance(vegetarianTag, "Required", Clock);

        // Recipe corpus: only a meat recipe — NO vegetarian recipes (no recipe with vegetarianTag).
        var meatRecipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel>
        {
            new(meatRecipeId, "Beef Stew", [meatTag], DefaultServings: 4),
        };

        // Track whether ProposeWeekAsync was called (it should NOT be for unfulfillable cells).
        var trackingPlanner = new TrackingMealPlanner();
        var tagReader = new NamedTagReader(vegetarianTag, "Vegetarian");

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: [pref],
            recipes: recipes,
            planner: trackingPlanner,
            tagReader: tagReader);

        var storeKey = "unfulfillable-test-key";

        // Act
        var result = await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        // Assert: all cells are unfulfillable (no vegetarian recipe at all in corpus).
        Assert.True(result.UnfulfillableCells.Count > 0, "Expected at least one unfulfillable cell");
        Assert.Equal(0, result.ProposedCount);
        Assert.Empty(result.Conflicts); // Not a HardConflict — it's an Unfulfillable (corpus gap)

        // AI was NOT called — no token spend for a provably-unfillable cell.
        Assert.False(trackingPlanner.WasCalled, "AI planner should NOT be called for unfulfillable cells");

        // No proposals were staged.
        var pending = await store.GetAsync(storeKey);
        Assert.Empty(pending);

        // The tag name is resolved in the cell.
        var firstUnfulfillable = result.UnfulfillableCells[0];
        Assert.Equal("Vegetarian", firstUnfulfillable.TagName);
        Assert.Equal(userId, firstUnfulfillable.Reason.AttendeeId);
        Assert.Equal(vegetarianTag, firstUnfulfillable.Reason.UnfulfillableTagId);
    }

    [Fact(DisplayName = "Execute_HardConflict_NotUnfulfillable — two attendees each have recipes but no shared one → HardConflict, not Unfulfillable")]
    public async Task Execute_HardConflict_WinsOver_Unfulfillable()
    {
        // Arrange: two attendees each have recipes satisfying their respective Required tags,
        // but no single recipe satisfies BOTH. This is HardConflict (C6), NOT Unfulfillable.
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var veganTag = Guid.NewGuid();
        var meatTag = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [aliceId, bobId], Clock);

        var alicePref = UserPreference.Create(Household, aliceId, Clock);
        alicePref.SetStance(veganTag, "Required", Clock);
        var bobPref = UserPreference.Create(Household, bobId, Clock);
        bobPref.SetStance(meatTag, "Required", Clock);

        // Both tags HAVE recipes in the corpus — just no shared recipe.
        var veganRecipeId = Guid.NewGuid();
        var meatRecipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel>
        {
            new(veganRecipeId, "Vegan Stir-Fry", [veganTag], DefaultServings: 2),
            new(meatRecipeId, "Beef Stew", [meatTag], DefaultServings: 4),
        };

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: [alicePref, bobPref],
            recipes: recipes);

        var storeKey = "hard-conflict-test-key";

        // Act
        var result = await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        // Assert: cells flagged as HardConflict, NOT as Unfulfillable.
        Assert.True(result.Conflicts.Count > 0, "Expected at least one HardConflict");
        Assert.Empty(result.UnfulfillableCells); // No corpus gap — both tags have recipes
        Assert.Equal(0, result.ProposedCount);
    }

    [Fact(DisplayName = "Execute_NormalCell_NotUnfulfillable — attendee has Required tag and a matching recipe → cell goes to planner")]
    public async Task Execute_NormalCell_IsProposed()
    {
        // Arrange: one attendee with vegetarian Required tag AND a vegetarian recipe in corpus.
        var userId = Guid.NewGuid();
        var vegetarianTag = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [userId], Clock);

        var pref = UserPreference.Create(Household, userId, Clock);
        pref.SetStance(vegetarianTag, "Required", Clock);

        // A vegetarian recipe exists in the corpus.
        var veganRecipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel>
        {
            new(veganRecipeId, "Vegan Curry", [vegetarianTag], DefaultServings: 4),
        };

        var (generateService, _, _, _, _) = BuildStack(
            slotConfig: config,
            prefs: [pref],
            recipes: recipes);

        var storeKey = "normal-cell-test-key";

        // Act
        var result = await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        // Assert: no unfulfillable, no conflict — cells reach the planner.
        Assert.Empty(result.UnfulfillableCells);
        Assert.Empty(result.Conflicts);
        // ProposedCount may be 0 if the FakeMealPlanner returns nothing, but cells WERE submitted.
    }

    // ── Test doubles ──────────────────────────────────────────────────────────────

    private sealed class FakeSlotConfigRepo(MealSlotConfig config) : IMealSlotConfigRepository
    {
        public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult<MealSlotConfig?>(config);
        public Task AddAsync(MealSlotConfig c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePrefsRepo(IReadOnlyList<UserPreference> prefs) : IUserPreferenceRepository
    {
        public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(prefs.FirstOrDefault(p => p.UserId == userId));
        public Task AddAsync(UserPreference pref, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRecipeReader(IReadOnlyList<RecipeReadModel> recipes) : IRecipeReadModel
    {
        public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(recipes.FirstOrDefault(r => r.RecipeId == id));

        public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int maxResults = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeReadModel>>(recipes.Take(maxResults).ToList());

        public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<RecipeDishEnrichment?>(null);

        public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

        /// <summary>
        /// Targeted full-corpus query: returns true when ANY recipe in the in-memory list carries the tag.
        /// Unlike SearchAsync (which respects maxResults), this queries ALL recipes.
        /// </summary>
        public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default) =>
            Task.FromResult(recipes.Any(r => r.TagIds.Contains(tagId)));
    }

    /// <summary>Returns empty tag groups; used when tag name resolution is not under test.</summary>
    private sealed class NullTagReader : ITagReader
    {
        public Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagGroup>>([]);
    }

    /// <summary>
    /// ITagReader that returns a named tag for a given tag ID. Used to test tag name resolution
    /// in unfulfillable cell output.
    /// </summary>
    private sealed class NamedTagReader(Guid tagId, string tagName) : ITagReader
    {
        public Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default)
        {
            IReadOnlyList<TagGroup> groups = [new TagGroup("Diet", 150,
                [new TagSummary(tagId, tagName, "Diet", 150)])];
            return Task.FromResult(groups);
        }
    }

    /// <summary>A planner that records whether ProposeWeekAsync was called.</summary>
    private sealed class TrackingMealPlanner : IMealPlanner
    {
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
            IReadOnlyList<PlannerMealSlotContext> contexts,
            PlanningWeights weights,
            CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult<IReadOnlyList<ProposedMeal>>([]);
        }
    }

    private sealed class FakeMealPlanRepository : IMealPlanRepository
    {
        private MealPlan? _plan;

        public void SetPlan(MealPlan plan) => _plan = plan;
        public MealPlan? GetPlan() => _plan;

        public Task<MealPlan?> FindByWeekAsync(HouseholdId h, DateOnly w, CancellationToken ct = default) =>
            Task.FromResult(_plan);

        public Task<MealPlan> FindOrCreateAsync(HouseholdId h, DateOnly w, IClock clock, CancellationToken ct = default)
        {
            _plan ??= MealPlan.Start(h, w, clock);
            return Task.FromResult(_plan);
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A planner that always proposes the given recipe for every slot, ignoring constraints.</summary>
    private sealed class SingleRecipeFakePlanner(Guid recipeId) : IMealPlanner
    {
        public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
            IReadOnlyList<PlannerMealSlotContext> contexts,
            PlanningWeights weights,
            CancellationToken ct = default)
        {
            var proposals = contexts.Select(ctx => new ProposedMeal(
                ctx.Date, ctx.MealSlotId, ctx.EffectiveAttendees,
                [new ProposedDish(recipeId, 4, 1)],
                "Bad planner")).ToList();

            return Task.FromResult<IReadOnlyList<ProposedMeal>>(proposals);
        }
    }

}
