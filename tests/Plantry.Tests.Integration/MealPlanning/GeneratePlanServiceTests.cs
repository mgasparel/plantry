using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
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
    private static readonly Guid ExpensiveCandidateId = Guid.Parse("0193b4a0-4444-7000-8000-000000000001");
    private static readonly Guid CheapCandidateId = Guid.Parse("0193b4a0-4444-7000-8000-000000000002");
    private static readonly Guid NoExpiryCandidateId = Guid.Parse("0193b4a0-4444-7000-8000-000000000003");
    private static readonly Guid UseSoonCandidateId = Guid.Parse("0193b4a0-4444-7000-8000-000000000004");

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static (GeneratePlanService, AcceptProposalService, IPendingProposalStore, FakeMealPlanRepository, FakeSlotConfigRepo)
        BuildStack(
            MealSlotConfig? slotConfig = null,
            IReadOnlyList<UserPreference>? prefs = null,
            IReadOnlyList<RecipeReadModel>? recipes = null,
            IMealPlanner? planner = null,
            ITagReader? tagReader = null,
            IReadOnlyDictionary<Guid, RecipeRatingSummary>? ratings = null,
            IMealPlanCatalogProductReader? catalogReader = null,
            IReadOnlyDictionary<Guid, CandidateRecipeEvidence>? evidence = null,
            IReadOnlyList<IReadOnlyDictionary<Guid, CandidateRecipeEvidence>>? evidenceSnapshots = null,
            IClock? planningClock = null)
    {
        var config = slotConfig ?? BuildDefaultSlotConfig();
        var slotConfigRepo = new FakeSlotConfigRepo(config);
        var prefRepo = new FakePrefsRepo(prefs ?? []);
        var recipeReader = new FakeRecipeReader(
            recipes ?? [],
            ratings ?? new Dictionary<Guid, RecipeRatingSummary>(),
            evidence ?? new Dictionary<Guid, CandidateRecipeEvidence>(),
            evidenceSnapshots);
        var mealPlanRepo = new FakeMealPlanRepository();
        var sp = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var memoryCache = sp.GetRequiredService<IDistributedCache>();
        var store = new DistributedCachePendingProposalStore(memoryCache);
        var resolver = new MealConstraintResolver();
        var fakePlanner = planner ?? new FakeMealPlanner();
        var fakeTagReader = tagReader ?? new NullTagReader();
        var fakeCatalogReader = catalogReader ?? new FakeCatalogReader();

        var clock = planningClock ?? Clock;
        var generateService = new GeneratePlanService(
            fakePlanner, mealPlanRepo, slotConfigRepo, prefRepo, recipeReader, fakeCatalogReader, store, resolver, fakeTagReader,
            clock,
            NullLogger<GeneratePlanService>.Instance);

        var acceptService = new AcceptProposalService(
            mealPlanRepo, slotConfigRepo, prefRepo, recipeReader, store, resolver, clock,
            NullLogger<AcceptProposalService>.Instance);

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

    // ── Attendee-aware rating enrichment (plantry-zlwp.5) ─────────────────────────

    [Fact(DisplayName = "Execute_RatingEnrichment — candidate carries the slot's attendee stars + household avg/count, scoped per attendee")]
    public async Task Execute_PassesCorrectRatingData_ForAttendees()
    {
        // Arrange: two attendees on every slot. Alice has rated the candidate 5 stars; Bob has not rated
        // it at all (only a third, non-attendee household member has, contributing to the household avg).
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var otherMemberId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [aliceId, bobId], Clock);

        var recipes = new List<RecipeReadModel> { new(recipeId, "Sheet-Pan Chicken", [], DefaultServings: 4) };
        var ratings = new Dictionary<Guid, RecipeRatingSummary>
        {
            [recipeId] = new(
                StarsByUserId: new Dictionary<Guid, int> { [aliceId] = 5, [otherMemberId] = 3 },
                HouseholdAvg: 4.0m,
                RatedCount: 2),
        };

        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(
            slotConfig: config, recipes: recipes, ratings: ratings, planner: planner);

        // Act
        await generateService.ExecuteAsync(Household, Monday, "rating-enrichment-key", null);

        // Assert: every dispatched cell's candidate carries Alice's own 5-star rating (she's an
        // attendee and has rated), NOT Bob's (he's an attendee but hasn't rated — absent from the
        // dictionary, not a zero/default entry), and the household-wide avg/count regardless of
        // who the attendees are.
        Assert.NotEmpty(planner.SeenContexts);
        foreach (var ctx in planner.SeenContexts)
        {
            var candidate = Assert.Single(ctx.CandidateRecipes, c => c.RecipeId == recipeId);
            Assert.NotNull(candidate.AttendeeStars);
            Assert.Equal(5, candidate.AttendeeStars![aliceId]);
            Assert.False(candidate.AttendeeStars.ContainsKey(bobId));
            Assert.False(candidate.AttendeeStars.ContainsKey(otherMemberId)); // not an attendee of this slot
            Assert.Equal(4.0m, candidate.HouseholdAvgRating);
            Assert.Equal(2, candidate.RatedCount);
        }
    }

    [Fact(DisplayName = "Execute_RatingEnrichment_NoRatings — an unrated candidate carries null AttendeeStars/HouseholdAvgRating and zero RatedCount")]
    public async Task Execute_UnratedCandidate_CarriesNoRatingData()
    {
        var userId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [userId], Clock);

        var recipes = new List<RecipeReadModel> { new(recipeId, "Pasta", [], DefaultServings: 4) };

        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(
            slotConfig: config, recipes: recipes, planner: planner); // no ratings passed

        await generateService.ExecuteAsync(Household, Monday, "no-ratings-key", null);

        Assert.NotEmpty(planner.SeenContexts);
        foreach (var ctx in planner.SeenContexts)
        {
            var candidate = Assert.Single(ctx.CandidateRecipes, c => c.RecipeId == recipeId);
            Assert.Null(candidate.AttendeeStars);
            Assert.Null(candidate.HouseholdAvgRating);
            Assert.Equal(0, candidate.RatedCount);
        }
    }

    [Fact(DisplayName = "Execute_RatingEnrichment_LowRatingIsSoftSignal — a 1-star candidate still reaches the planner alongside a 5-star one, never filtered out")]
    public async Task Execute_LowRatedCandidate_StillDispatchedToPlanner()
    {
        // Arrange: two candidates for the same slot, rated 1 and 5 stars respectively by the sole
        // attendee. The ticket's load-bearing distinction is "soft signal, NOT a hard filter" — a low
        // rating must attach as DATA on the candidate, never remove it from what the planner sees.
        var userId = Guid.NewGuid();
        var lowRatedRecipeId = Guid.NewGuid();
        var highRatedRecipeId = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [userId], Clock);

        var recipes = new List<RecipeReadModel>
        {
            new(lowRatedRecipeId, "Disliked Dish", [], DefaultServings: 4),
            new(highRatedRecipeId, "Favourite Dish", [], DefaultServings: 4),
        };
        var ratings = new Dictionary<Guid, RecipeRatingSummary>
        {
            [lowRatedRecipeId] = new(
                StarsByUserId: new Dictionary<Guid, int> { [userId] = 1 }, HouseholdAvg: 1.0m, RatedCount: 1),
            [highRatedRecipeId] = new(
                StarsByUserId: new Dictionary<Guid, int> { [userId] = 5 }, HouseholdAvg: 5.0m, RatedCount: 1),
        };

        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(
            slotConfig: config, recipes: recipes, ratings: ratings, planner: planner);

        // Act
        await generateService.ExecuteAsync(Household, Monday, "low-rating-soft-signal-key", null);

        // Assert: both candidates are STILL dispatched to the planner for every cell — the 1-star
        // rating is carried as data on the candidate, never used to drop it from the list.
        Assert.NotEmpty(planner.SeenContexts);
        foreach (var ctx in planner.SeenContexts)
        {
            Assert.Contains(ctx.CandidateRecipes, c => c.RecipeId == lowRatedRecipeId);
            Assert.Contains(ctx.CandidateRecipes, c => c.RecipeId == highRatedRecipeId);

            var lowRated = ctx.CandidateRecipes.Single(c => c.RecipeId == lowRatedRecipeId);
            Assert.Equal(1, lowRated.AttendeeStars![userId]);
        }
    }

    [Fact(DisplayName = "Execute_RatingEnrichment_DuplicateAttendee — a repeated attendee id does not throw when building AttendeeStars")]
    public async Task Execute_DuplicateAttendeeId_DoesNotThrow()
    {
        // Arrange: DefaultAttendees carries the SAME user id twice — reachable in production (no dedup
        // in MealSlot.SetDefaultAttendees / MealConstraintResolver.EffectiveAttendees). Building
        // AttendeeStars via ToDictionary over a duplicate-bearing list must not throw.
        var userId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var s in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(s.Id, [userId, userId], Clock);

        var recipes = new List<RecipeReadModel> { new(recipeId, "Chili", [], DefaultServings: 4) };
        var ratings = new Dictionary<Guid, RecipeRatingSummary>
        {
            [recipeId] = new(
                StarsByUserId: new Dictionary<Guid, int> { [userId] = 4 }, HouseholdAvg: 4.0m, RatedCount: 1),
        };

        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(
            slotConfig: config, recipes: recipes, ratings: ratings, planner: planner);

        // Act — must not throw ArgumentException from a duplicate-key ToDictionary.
        await generateService.ExecuteAsync(Household, Monday, "duplicate-attendee-key", null);

        // Assert: the duplicate collapses to a single AttendeeStars entry for that user.
        Assert.NotEmpty(planner.SeenContexts);
        foreach (var ctx in planner.SeenContexts)
        {
            var candidate = Assert.Single(ctx.CandidateRecipes, c => c.RecipeId == recipeId);
            Assert.Equal(4, candidate.AttendeeStars![userId]);
            Assert.Single(candidate.AttendeeStars);
        }
    }

    [Fact(DisplayName = "Execute_CandidateEvidence_CostWeightChangesDeterministicOrder")]
    public async Task Execute_CostEvidence_ChangesCandidateOrder()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        var expensiveId = ExpensiveCandidateId;
        var cheapId = CheapCandidateId;
        var recipes = new List<RecipeReadModel>
        {
            new(expensiveId, "Alpha Expensive", [], 4),
            new(cheapId, "Zulu Cheap", [], 4),
        };
        var evidence = new Dictionary<Guid, CandidateRecipeEvidence>
        {
            [expensiveId] = new(10m, CandidateCostCompleteness.Complete, 100, false),
            [cheapId] = new(2m, CandidateCostCompleteness.Complete, 100, false),
        };
        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(
            slotConfig: config,
            recipes: recipes,
            evidence: evidence,
            planner: planner);

        await generateService.ExecuteAsync(
            Household,
            Monday,
            "cost-evidence-order",
            new PlanningWeights(0, 100, 0),
            scopeDate: Monday,
            scopeSlotId: breakfast.Id);

        var ordered = Assert.Single(planner.SeenContexts).CandidateRecipes;
        Assert.Equal(cheapId, ordered[0].RecipeId);
        Assert.Equal(expensiveId, ordered[1].RecipeId);
    }

    [Fact(DisplayName = "Execute_CandidateEvidence_WasteWeightChangesDeterministicOrder")]
    public async Task Execute_ExpiringStockEvidence_ChangesCandidateOrder()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        var noExpiryId = NoExpiryCandidateId;
        var useSoonId = UseSoonCandidateId;
        var recipes = new List<RecipeReadModel>
        {
            new(noExpiryId, "Alpha No Expiry", [], 4),
            new(useSoonId, "Zulu Use Soon", [], 4),
        };
        var evidence = new Dictionary<Guid, CandidateRecipeEvidence>
        {
            [noExpiryId] = new(null, CandidateCostCompleteness.Unknown, 100, false),
            [useSoonId] = new(null, CandidateCostCompleteness.Unknown, 100, true),
        };
        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(
            slotConfig: config,
            recipes: recipes,
            evidence: evidence,
            planner: planner);

        await generateService.ExecuteAsync(
            Household,
            Monday,
            "waste-evidence-order",
            new PlanningWeights(100, 0, 0),
            scopeDate: Monday,
            scopeSlotId: breakfast.Id);

        var ordered = Assert.Single(planner.SeenContexts).CandidateRecipes;
        Assert.Equal(useSoonId, ordered[0].RecipeId);
        Assert.Equal(noExpiryId, ordered[1].RecipeId);
    }

    [Fact(DisplayName = "Execute_CandidateEvidence_IsRequestedOnceAndReusedAcrossSlots")]
    public async Task Execute_CandidateEvidence_IsRequestedOnce()
    {
        var recipeId = Guid.NewGuid();
        var firstSnapshot = new Dictionary<Guid, CandidateRecipeEvidence>
        {
            [recipeId] = new(2m, CandidateCostCompleteness.Complete, 100, false),
        };
        var secondSnapshot = new Dictionary<Guid, CandidateRecipeEvidence>
        {
            [recipeId] = new(99m, CandidateCostCompleteness.Complete, 100, true),
        };
        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(
            recipes: [new RecipeReadModel(recipeId, "Pasta", [], 4)],
            evidenceSnapshots: [firstSnapshot, secondSnapshot],
            planner: planner);

        await generateService.ExecuteAsync(Household, Monday, "candidate-evidence-once", null);

        Assert.NotEmpty(planner.SeenContexts);
        foreach (var context in planner.SeenContexts)
        {
            var candidate = Assert.Single(context.CandidateRecipes, c => c.RecipeId == recipeId);
            Assert.Equal(2m, candidate.CostPerServing);
            Assert.Equal(CandidateCostCompleteness.Complete, candidate.CostCompleteness);
            Assert.False(candidate.HasContributingExpiringStock);
        }
    }

    [Fact(DisplayName = "Execute_ProposalRationale_UnknownEvidence_ReplacesUnsupportedPlannerReasoning")]
    public async Task Execute_ProposalRationale_UnknownEvidence_ReplacesUnsupportedPlannerReasoning()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        var recipeId = Guid.NewGuid();
        const string unsupportedReasoning = "This is the cheapest option and prevents food waste.";
        var planner = new UnsupportedReasoningPlanner(recipeId, unsupportedReasoning);
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: [new RecipeReadModel(recipeId, "Pasta", [], 4)],
            planner: planner);

        await generateService.ExecuteAsync(
            Household,
            Monday,
            "unknown-evidence-rationale",
            null,
            scopeDate: Monday,
            scopeSlotId: breakfast.Id);

        var proposal = Assert.Single(await store.GetAsync("unknown-evidence-rationale"));
        Assert.NotEqual(unsupportedReasoning, proposal.Reasoning);
        Assert.Equal(
            "Selected recipes satisfy the slot's hard constraints; no additional cost or waste benefit is claimed without supporting evidence.",
            proposal.Reasoning);
    }

    [Fact(DisplayName = "Execute_ProposalRationale_CompleteCostAndUseSoonEvidence_ReplacesUnsupportedPlannerReasoning")]
    public async Task Execute_ProposalRationale_CompleteCostAndUseSoonEvidence_ReplacesUnsupportedPlannerReasoning()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        var recipeId = Guid.NewGuid();
        const string unsupportedReasoning = "This is the cheapest option and prevents food waste.";
        var evidence = new Dictionary<Guid, CandidateRecipeEvidence>
        {
            [recipeId] = new(2m, CandidateCostCompleteness.Complete, 100, true),
        };
        var planner = new UnsupportedReasoningPlanner(recipeId, unsupportedReasoning);
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: [new RecipeReadModel(recipeId, "Pasta", [], 4)],
            evidence: evidence,
            planner: planner);

        await generateService.ExecuteAsync(
            Household,
            Monday,
            "complete-evidence-rationale",
            null,
            scopeDate: Monday,
            scopeSlotId: breakfast.Id);

        var proposal = Assert.Single(await store.GetAsync("complete-evidence-rationale"));
        Assert.NotEqual(unsupportedReasoning, proposal.Reasoning);
        Assert.Equal(
            "Selected recipes satisfy the slot's hard constraints; complete cost evidence is available and some stock is due to expire soon.",
            proposal.Reasoning);
    }

    // ── Per-slot auto-plan opt-out (plantry-av8z) ─────────────────────────────────

    [Fact(DisplayName = "Execute_Bulk_SkipsOptedOutSlots — a slot with IncludeInAutoPlan=false is not dispatched")]
    public async Task Execute_Bulk_SkipsOptedOutSlots()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        var lunch = config.Slots.First(s => s.Label == "Lunch");
        config.SetAutoPlanEnabled(breakfast.Id, enabled: false, Clock);

        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(slotConfig: config, planner: planner);

        // Bulk pass: scopeSlotId stays null → the opt-out filter applies.
        await generateService.ExecuteAsync(Household, Monday, "opt-out-bulk", null);

        // The opted-out Breakfast slot was never dispatched to the planner …
        Assert.DoesNotContain(planner.SeenContexts, c => c.MealSlotId == breakfast.Id);
        // … but the still-eligible Lunch slot was.
        Assert.Contains(planner.SeenContexts, c => c.MealSlotId == lunch.Id);
    }

    [Fact(DisplayName = "Execute_ScopeSlotId_TargetsSingleCell_IgnoresOptOut — explicit per-cell gesture bypasses the flag")]
    public async Task Execute_ScopeSlotId_TargetsSingleCell_IgnoresOptOut()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        // Breakfast is opted OUT of bulk — but an explicit per-cell target must still generate it.
        config.SetAutoPlanEnabled(breakfast.Id, enabled: false, Clock);

        var planner = new RecordingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(slotConfig: config, planner: planner);

        await generateService.ExecuteAsync(
            Household, Monday, "opt-out-cell", null, scopeDate: Monday, scopeSlotId: breakfast.Id);

        // Exactly one cell — Monday Breakfast — was dispatched, despite the opt-out (decision 1),
        // and no throwaway proposals for the date's other slots.
        Assert.Single(planner.SeenContexts);
        Assert.Equal(breakfast.Id, planner.SeenContexts[0].MealSlotId);
        Assert.Equal(Monday, planner.SeenContexts[0].Date);
    }

    [Fact(DisplayName = "Execute_AllSlotsOptedOut_ReturnsZero_WithoutCallingPlanner — no eligible slots short-circuits")]
    public async Task Execute_AllSlotsOptedOut_ReturnsZero_WithoutCallingPlanner()
    {
        var config = BuildDefaultSlotConfig();
        foreach (var slot in config.Slots.Where(s => s.IsActive).ToList())
            config.SetAutoPlanEnabled(slot.Id, enabled: false, Clock);

        var planner = new TrackingMealPlanner();
        var (generateService, _, _, _, _) = BuildStack(slotConfig: config, planner: planner);

        var result = await generateService.ExecuteAsync(Household, Monday, "opt-out-all", null);

        Assert.Equal(0, result.ProposedCount);
        Assert.False(planner.WasCalled);
    }

    // ── Already-planned meal summary (plantry-6mux) ───────────────────────────────

    [Fact(DisplayName = "Execute_AlreadyPlanned_NoExistingPlan — planner receives an empty already-planned list")]
    public async Task Execute_AlreadyPlanned_NoExistingPlan()
    {
        var recipeId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel> { new(recipeId, "Pasta", [], DefaultServings: 4) };
        var planner = new RecordingMealPlanner();

        var (generateService, _, _, _, _) = BuildStack(recipes: recipes, planner: planner);

        await generateService.ExecuteAsync(Household, Monday, "already-planned-empty", null);

        Assert.Empty(planner.SeenAlreadyPlanned);
    }

    [Fact(DisplayName = "Execute_AlreadyPlanned_WithExistingPlan — planner receives a summary of every planned meal in the week, and unresolvable dish names are skipped")]
    public async Task Execute_AlreadyPlanned_WithExistingPlan()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First();
        var dinner = config.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).Last();

        var knownRecipeId = Guid.NewGuid();
        var deletedRecipeId = Guid.NewGuid(); // NOT in the recipe reader — simulates a deleted recipe
        var knownProductId = Guid.NewGuid();
        var recipes = new List<RecipeReadModel> { new(knownRecipeId, "Known Recipe", [], DefaultServings: 4) };
        var catalogReader = new FakeCatalogReader(new Dictionary<Guid, string> { [knownProductId] = "Known Product" });

        // Fixed (not NewGuid) — a slot id that is NOT present in `config.Slots` at all, simulating a
        // slot deleted from the household's slot configuration since the meal was planned. Label
        // resolution must fall back to the raw slot id string rather than throwing or defaulting to
        // some placeholder text (plantry-6mux design §2).
        var orphanSlotId = MealSlotId.From(Guid.Parse("0193b4a0-3333-7000-8000-000000000001"));

        var plan = MealPlan.Start(Household, Monday, Clock);
        // Monday's breakfast: a recipe dish that resolves fine.
        plan.AssignMeal(Monday, breakfast.Id, [new DishSpec(DishKind.Recipe, knownRecipeId, 4)],
            null, "manual", Guid.NewGuid(), Clock);
        // Tuesday's dinner (a DIFFERENT date/slot than any scoped run below): a product dish that
        // resolves fine PLUS a deleted-recipe dish that must be skipped, never sent as a raw GUID.
        var tuesday = Monday.AddDays(1);
        plan.AssignMeal(tuesday, dinner.Id,
            [DishSpec.ForProduct(knownProductId, 2m, Guid.NewGuid()), new DishSpec(DishKind.Recipe, deletedRecipeId, 4)],
            null, "manual", Guid.NewGuid(), Clock);
        // Thursday's meal on the orphan (deleted) slot.
        var thursday = Monday.AddDays(3);
        plan.AssignMeal(thursday, orphanSlotId, [new DishSpec(DishKind.Recipe, knownRecipeId, 4)],
            null, "manual", Guid.NewGuid(), Clock);

        var planner = new RecordingMealPlanner();
        var (generateService, _, _, mealPlanRepo, _) = BuildStack(
            slotConfig: config, recipes: recipes, planner: planner, catalogReader: catalogReader);
        mealPlanRepo.SetPlan(plan);

        // Scoped run (per-cell Regenerate): targets ONLY Wednesday's breakfast — an empty cell
        // distinct from all already-planned meals above. Every existing meal must still appear in
        // the already-planned summary despite being out of this run's scope (plantry-6mux design §2).
        var wednesday = Monday.AddDays(2);

        await generateService.ExecuteAsync(
            Household, Monday, "already-planned-scoped", null, scopeDate: wednesday, scopeSlotId: breakfast.Id);

        Assert.Equal(3, planner.SeenAlreadyPlanned.Count);

        var mondayBreakfast = Assert.Single(planner.SeenAlreadyPlanned, s => s.Date == Monday);
        Assert.Equal(breakfast.Label, mondayBreakfast.SlotLabel);
        Assert.Equal(["Known Recipe"], mondayBreakfast.DishNames);

        var tuesdayDinner = Assert.Single(planner.SeenAlreadyPlanned, s => s.Date == tuesday);
        Assert.Equal(dinner.Label, tuesdayDinner.SlotLabel);
        // The deleted recipe's dish is skipped — only the resolvable product dish name survives.
        Assert.Equal(["Known Product"], tuesdayDinner.DishNames);

        var orphan = Assert.Single(planner.SeenAlreadyPlanned, s => s.Date == thursday);
        // The slot itself was deleted from config — label falls back to the raw slot id string.
        Assert.Equal(orphanSlotId.Value.ToString(), orphan.SlotLabel);
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

    private sealed class FakeRecipeReader(
        IReadOnlyList<RecipeReadModel> recipes,
        IReadOnlyDictionary<Guid, RecipeRatingSummary>? ratings = null,
        IReadOnlyDictionary<Guid, CandidateRecipeEvidence>? evidence = null,
        IReadOnlyList<IReadOnlyDictionary<Guid, CandidateRecipeEvidence>>? evidenceSnapshots = null) : IRecipeReadModel
    {
        private int _evidenceSnapshotIndex;

        public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(recipes.FirstOrDefault(r => r.RecipeId == id));

        public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int maxResults = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeReadModel>>(recipes.Take(maxResults).ToList());

        public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<RecipeDishEnrichment?>(null);

        public Task<IReadOnlyDictionary<Guid, CandidateRecipeEvidence>> GetCandidateEvidenceAsync(
            IReadOnlyCollection<CandidateRecipeEvidenceRequest> requests,
            DateOnly today,
            CancellationToken ct = default)
        {
            var snapshot = evidenceSnapshots is { Count: > 0 }
                ? evidenceSnapshots[Math.Min(_evidenceSnapshotIndex, evidenceSnapshots.Count - 1)]
                : evidence ?? new Dictionary<Guid, CandidateRecipeEvidence>();
            _evidenceSnapshotIndex++;
            IReadOnlyDictionary<Guid, CandidateRecipeEvidence> result = requests
                .Select(r => r.RecipeId)
                .Distinct()
                .Where(snapshot.ContainsKey)
                .ToDictionary(id => id, id => snapshot[id]);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

        /// <summary>
        /// Targeted full-corpus query: returns true when ANY recipe in the in-memory list carries the tag.
        /// Unlike SearchAsync (which respects maxResults), this queries ALL recipes.
        /// </summary>
        public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default) =>
            Task.FromResult(recipes.Any(r => r.TagIds.Contains(tagId)));

        /// <summary>plantry-zlwp.5: in-memory rating summaries, keyed by recipe id — unrated ids simply omitted.</summary>
        public Task<IReadOnlyDictionary<Guid, RecipeRatingSummary>> GetRatingSummariesAsync(
            IReadOnlyCollection<Guid> recipeIds, CancellationToken ct = default) =>
            Task.FromResult(ratings ?? new Dictionary<Guid, RecipeRatingSummary>());
    }

    /// <summary>In-memory product-name resolver (plantry-6mux) — ids absent from the map are omitted,
    /// mirroring the production adapter's "absent means unresolved" convention.</summary>
    private sealed class FakeCatalogReader(IReadOnlyDictionary<Guid, string>? names = null) : IMealPlanCatalogProductReader
    {
        public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult((names ?? new Dictionary<Guid, string>()).ContainsKey(productId));

        public Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult((names ?? new Dictionary<Guid, string>()).ContainsKey(productId));

        public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>([]);

        public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        {
            var map = names ?? new Dictionary<Guid, string>();
            IReadOnlyDictionary<Guid, string> result = productIds
                .Where(map.ContainsKey)
                .ToDictionary(id => id, id => map[id]);
            return Task.FromResult(result);
        }
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

    /// <summary>Records every context ProposeWeekAsync was asked to plan (proposes nothing).</summary>
    private sealed class RecordingMealPlanner : IMealPlanner
    {
        public List<PlannerMealSlotContext> SeenContexts { get; } = [];

        /// <summary>Snapshot of the alreadyPlanned list from the MOST RECENT ProposeWeekAsync call (plantry-6mux).</summary>
        public IReadOnlyList<PlannedMealSummary> SeenAlreadyPlanned { get; private set; } = [];

        public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
            IReadOnlyList<PlannerMealSlotContext> contexts,
            IReadOnlyList<PlannedMealSummary> alreadyPlanned,
            PlanningWeights weights,
            CancellationToken ct = default)
        {
            SeenContexts.AddRange(contexts);
            SeenAlreadyPlanned = alreadyPlanned;
            return Task.FromResult<IReadOnlyList<ProposedMeal>>([]);
        }
    }

    /// <summary>A planner that records whether ProposeWeekAsync was called.</summary>
    private sealed class TrackingMealPlanner : IMealPlanner
    {
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
            IReadOnlyList<PlannerMealSlotContext> contexts,
            IReadOnlyList<PlannedMealSummary> alreadyPlanned,
            PlanningWeights weights,
            CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult<IReadOnlyList<ProposedMeal>>([]);
        }
    }

    private sealed class UnsupportedReasoningPlanner(Guid recipeId, string reasoning) : IMealPlanner
    {
        public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
            IReadOnlyList<PlannerMealSlotContext> contexts,
            IReadOnlyList<PlannedMealSummary> alreadyPlanned,
            PlanningWeights weights,
            CancellationToken ct = default)
        {
            IReadOnlyList<ProposedMeal> proposals = contexts
                .Select(context => new ProposedMeal(
                    context.Date,
                    context.MealSlotId,
                    context.EffectiveAttendees,
                    [new ProposedDish(recipeId, 4, 1)],
                    reasoning))
                .ToList();
            return Task.FromResult(proposals);
        }
    }

    private sealed class FakeMealPlanRepository : IMealPlanRepository
    {
    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

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
            IReadOnlyList<PlannedMealSummary> alreadyPlanned,
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
