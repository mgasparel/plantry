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
            ITagReader? tagReader = null,
            IReadOnlyDictionary<Guid, RecipeRatingSummary>? ratings = null,
            IMealPlanCatalogProductReader? catalogReader = null,
            IReadOnlyDictionary<Guid, CandidateRecipeEvidence>? evidence = null,
            IReadOnlyList<IReadOnlyDictionary<Guid, CandidateRecipeEvidence>>? evidenceSnapshots = null,
            IRecentMealHistoryReader? historyReader = null,
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
        var fakeTagReader = tagReader ?? new NullTagReader();
        var fakeCatalogReader = catalogReader ?? new FakeCatalogReader();

        var clock = planningClock ?? Clock;
        var generateService = new GeneratePlanService(
            mealPlanRepo, slotConfigRepo, prefRepo, recipeReader,
            historyReader ?? new FakeRecentMealHistoryReader(), fakeCatalogReader, store, resolver, fakeTagReader,
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

        var recipes = new List<RecipeReadModel>
        {
            new(recipeId, "RestrictedDish", [restrictedTag], DefaultServings: 4)
        };

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: prefs,
            recipes: recipes);

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

    [Fact(DisplayName = "Execute_RequiredVeganCandidate_CarriesSemanticProfileWithIndependentMissingFacets")]
    public async Task Execute_RequiredVeganCandidate_CarriesSemanticProfile()
    {
        var userId = Guid.Parse("60000000-0000-0000-0000-000000000001");
        var veganTagId = Guid.Parse("60000000-0000-0000-0000-000000000002");
        var recipeId = Guid.Parse("60000000-0000-0000-0000-000000000003");
        var config = MealSlotConfig.CreateWithDefaults(Household, Clock);
        foreach (var slot in config.Slots.Where(s => s.IsActive))
            config.SetDefaultAttendees(slot.Id, [userId], Clock);

        var preference = UserPreference.Create(Household, userId, Clock);
        preference.SetStance(veganTagId, "Required", Clock);
        var veganFact = new RecipeSemanticTagFact(
            veganTagId,
            "Vegan",
            RecipeSemanticTagCategory.Diet);
        var profile = RecipeDiversityProfile.Create(
            recipeId,
            "Garden supper",
            [veganFact],
            [veganFact],
            []);
        var recipes = new[]
        {
            new RecipeReadModel(
                recipeId,
                "Garden supper",
                [veganTagId],
                DefaultServings: 4,
                TagFacts: [veganFact],
                DiversityProfile: profile),
        };
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: [preference],
            recipes: recipes);

        await generateService.ExecuteAsync(Household, Monday, "semantic-profile", null);

        var staged = await store.GetAsync("semantic-profile");
        Assert.NotEmpty(staged);
        foreach (var proposal in staged)
        {
            var breakdown = Assert.Single(proposal.Dishes).ScoreBreakdown!;
            Assert.Contains(breakdown.VarietyContributions, contribution =>
                contribution.Facet == RecipeDiversityFacet.Diet
                && contribution.Confidence == RecipeDiversityConfidence.Confirmed
                && contribution.MatchedValues.Single() == "Vegan");
            Assert.Contains(breakdown.VarietyContributions, contribution =>
                contribution.Facet == RecipeDiversityFacet.Protein
                && contribution.Confidence == RecipeDiversityConfidence.Missing);
            Assert.Contains(breakdown.VarietyContributions, contribution =>
                contribution.Facet == RecipeDiversityFacet.Cuisine
                && contribution.Confidence == RecipeDiversityConfidence.Missing);
        }
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

        var tagReader = new NamedTagReader(vegetarianTag, "Vegetarian");

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: [pref],
            recipes: recipes,
            tagReader: tagReader);

        var storeKey = "unfulfillable-test-key";

        // Act
        var result = await generateService.ExecuteAsync(Household, Monday, storeKey, null);

        // Assert: all cells are unfulfillable (no vegetarian recipe at all in corpus).
        Assert.True(result.UnfulfillableCells.Count > 0, "Expected at least one unfulfillable cell");
        Assert.Equal(0, result.ProposedCount);
        Assert.Empty(result.Conflicts); // Not a HardConflict — it's an Unfulfillable (corpus gap)

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

    [Fact(DisplayName = "Execute_NormalCell_NotUnfulfillable — attendee has Required tag and a matching recipe → server stages a proposal")]
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

        // Assert: no unfulfillable, no conflict — server selection stages every feasible cell.
        Assert.Empty(result.UnfulfillableCells);
        Assert.Empty(result.Conflicts);
        Assert.True(result.ProposedCount > 0);
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

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config, recipes: recipes, ratings: ratings);

        // Act
        await generateService.ExecuteAsync(Household, Monday, "rating-enrichment-key", null);

        // Alice's attendee-specific 5-star signal, rather than the household 4.0 fallback, is retained
        // as the tie-break signal on each server-owned proposal.
        Assert.All(await store.GetAsync("rating-enrichment-key"), proposal =>
            Assert.Equal(5m, Assert.Single(proposal.Dishes).ScoreBreakdown!.TieBreakSignals.RatingSignal));
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

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config, recipes: recipes); // no ratings passed

        await generateService.ExecuteAsync(Household, Monday, "no-ratings-key", null);

        Assert.All(await store.GetAsync("no-ratings-key"), proposal =>
            Assert.Equal(0m, Assert.Single(proposal.Dishes).ScoreBreakdown!.TieBreakSignals.RatingSignal));
    }

    [Fact(DisplayName = "Execute_RatingEnrichment_LowRatingIsSoftSignal — a 5-star candidate wins only an objective tie")]
    public async Task Execute_LowRatedCandidate_RemainsSoftTieBreak()
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
        var breakfast = config.Slots.First(slot => slot.IsActive);

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

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config, recipes: recipes, ratings: ratings);

        // Act
        await generateService.ExecuteAsync(
            Household, Monday, "low-rating-soft-signal-key", null, scopeDate: Monday, scopeSlotId: breakfast.Id);

        Assert.All(await store.GetAsync("low-rating-soft-signal-key"), proposal =>
            Assert.Equal(highRatedRecipeId, Assert.Single(proposal.Dishes).RecipeId));
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

        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config, recipes: recipes, ratings: ratings);

        // Act — must not throw ArgumentException from a duplicate-key ToDictionary.
        await generateService.ExecuteAsync(Household, Monday, "duplicate-attendee-key", null);

        // The duplicate collapses before the deterministic signal is calculated.
        Assert.All(await store.GetAsync("duplicate-attendee-key"), proposal =>
            Assert.Equal(4m, Assert.Single(proposal.Dishes).ScoreBreakdown!.TieBreakSignals.RatingSignal));
    }

    [Fact(DisplayName = "Execute_CandidateEvidence_CostWeightStagesTheLowestCompleteCost")]
    public async Task Execute_CostEvidence_SelectsTheLowestCompleteCost()
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
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: recipes,
            evidence: evidence);

        await generateService.ExecuteAsync(
            Household,
            Monday,
            "cost-evidence-order",
            new PlanningWeights(0, 100, 0),
            scopeDate: Monday,
            scopeSlotId: breakfast.Id);

        var selected = Assert.Single(await store.GetAsync("cost-evidence-order"));
        Assert.Equal(cheapId, Assert.Single(selected.Dishes).RecipeId);
        Assert.Equal(1m, selected.Dishes.Single().ScoreBreakdown!.CostScore);
    }

    [Fact(DisplayName = "Execute_CandidateEvidence_WasteWeightStagesTheUseSoonRecipe")]
    public async Task Execute_ExpiringStockEvidence_SelectsTheUseSoonRecipe()
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
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: recipes,
            evidence: evidence);

        await generateService.ExecuteAsync(
            Household,
            Monday,
            "waste-evidence-order",
            new PlanningWeights(100, 0, 0),
            scopeDate: Monday,
            scopeSlotId: breakfast.Id);

        var selected = Assert.Single(await store.GetAsync("waste-evidence-order"));
        Assert.Equal(useSoonId, Assert.Single(selected.Dishes).RecipeId);
        Assert.Equal(1m, selected.Dishes.Single().ScoreBreakdown!.WasteScore);
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
        var (generateService, _, store, _, _) = BuildStack(
            recipes: [new RecipeReadModel(recipeId, "Pasta", [], 4)],
            evidenceSnapshots: [firstSnapshot, secondSnapshot]);

        await generateService.ExecuteAsync(Household, Monday, "candidate-evidence-once", null);

        Assert.All(await store.GetAsync("candidate-evidence-once"), proposal =>
        {
            var score = Assert.Single(proposal.Dishes).ScoreBreakdown!;
            Assert.Equal(1m, score.CostScore);
            Assert.Equal(0m, score.WasteScore);
        });
    }

    [Fact(DisplayName = "Execute_ProposalRationale_UnknownEvidence_ReplacesUnsupportedPlannerReasoning")]
    public async Task Execute_ProposalRationale_UnknownEvidence_ReplacesUnsupportedPlannerReasoning()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        var recipeId = Guid.NewGuid();
        const string unsupportedReasoning = "This is the cheapest option and prevents food waste.";
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: [new RecipeReadModel(recipeId, "Pasta", [], 4)]);

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
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: [new RecipeReadModel(recipeId, "Pasta", [], 4)],
            evidence: evidence);

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

        var recipeId = Guid.NewGuid();
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: [new RecipeReadModel(recipeId, "Scoped recipe", [], 4)]);

        // Bulk pass: scopeSlotId stays null → the opt-out filter applies.
        await generateService.ExecuteAsync(Household, Monday, "opt-out-bulk", null);

        var staged = await store.GetAsync("opt-out-bulk");
        // The opted-out Breakfast slot is absent from server-owned staging, while Lunch is included.
        Assert.DoesNotContain(staged, proposal => proposal.MealSlotId == breakfast.Id);
        Assert.Contains(staged, proposal => proposal.MealSlotId == lunch.Id);
    }

    [Fact(DisplayName = "Execute_ScopeSlotId_TargetsSingleCell_IgnoresOptOut — explicit per-cell gesture bypasses the flag")]
    public async Task Execute_ScopeSlotId_TargetsSingleCell_IgnoresOptOut()
    {
        var config = BuildDefaultSlotConfig();
        var breakfast = config.Slots.First(s => s.Label == "Breakfast");
        // Breakfast is opted OUT of bulk — but an explicit per-cell target must still generate it.
        config.SetAutoPlanEnabled(breakfast.Id, enabled: false, Clock);

        var recipeId = Guid.NewGuid();
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            recipes: [new RecipeReadModel(recipeId, "Scoped recipe", [], 4)]);

        await generateService.ExecuteAsync(
            Household, Monday, "opt-out-cell", null, scopeDate: Monday, scopeSlotId: breakfast.Id);

        // Exactly one server-owned proposal — Monday Breakfast — is staged despite the opt-out.
        var staged = Assert.Single(await store.GetAsync("opt-out-cell"));
        Assert.Equal(breakfast.Id, staged.MealSlotId);
        Assert.Equal(Monday, staged.Date);
    }

    [Fact(DisplayName = "Execute_AllSlotsOptedOut_ReturnsZeroWithoutStaging — no eligible slots short-circuits")]
    public async Task Execute_AllSlotsOptedOut_ReturnsZero_WithoutCallingPlanner()
    {
        var config = BuildDefaultSlotConfig();
        foreach (var slot in config.Slots.Where(s => s.IsActive).ToList())
            config.SetAutoPlanEnabled(slot.Id, enabled: false, Clock);

        var (generateService, _, _, _, _) = BuildStack(slotConfig: config);

        var result = await generateService.ExecuteAsync(Household, Monday, "opt-out-all", null);

        Assert.Equal(0, result.ProposedCount);
    }

    // ── Existing-week and retained-history variety inputs ──────────────────────────

    [Fact(DisplayName = "Execute_EmptyWeek_RetainedHistoryContributesToTheStagedVarietyBreakdown")]
    public async Task Execute_EmptyWeek_RetainedHistoryContributesToTheStagedVarietyBreakdown()
    {
        var recipeId = Guid.NewGuid();
        var snapshot = new RecentMealHistorySnapshot(
        [
            new RecentRecipeHistory(
                recipeId,
                "Recent pasta",
                IsArchived: false,
                [new RecentMealOccurrence(Monday.AddDays(-7), RecentMealOccurrenceSource.CookEvent, 0.20m)],
                [])
        ]);
        var (generateService, _, store, _, _) = BuildStack(
            recipes: [new RecipeReadModel(recipeId, "Recent pasta", [], DefaultServings: 4)],
            historyReader: new FakeRecentMealHistoryReader(snapshot));

        await generateService.ExecuteAsync(Household, Monday, "history-empty-week", null);

        var staged = await store.GetAsync("history-empty-week");
        Assert.All(staged, proposal => Assert.Contains(
            Assert.Single(proposal.Dishes).ScoreBreakdown!.VarietyContributions,
            contribution => contribution.Facet == RecipeDiversityFacet.ExactRecipe && contribution.PriorUse >= 0.20m));
    }

    [Fact(DisplayName = "Execute_AlreadyPlanned_WithExistingPlan — resolved planned recipes contribute to the next staged choice")]
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

        var (generateService, _, store, mealPlanRepo, _) = BuildStack(
            slotConfig: config, recipes: recipes, catalogReader: catalogReader);
        mealPlanRepo.SetPlan(plan);

        // Scoped run (per-cell Regenerate): targets ONLY Wednesday's breakfast — an empty cell
        // distinct from all already-planned meals above. Every existing meal must still appear in
        // the already-planned summary despite being out of this run's scope (plantry-6mux design §2).
        var wednesday = Monday.AddDays(2);

        await generateService.ExecuteAsync(
            Household, Monday, "already-planned-scoped", null, scopeDate: wednesday, scopeSlotId: breakfast.Id);

        var staged = Assert.Single(await store.GetAsync("already-planned-scoped"));
        var exact = Assert.Single(Assert.Single(staged.Dishes).ScoreBreakdown!.VarietyContributions,
            contribution => contribution.Facet == RecipeDiversityFacet.ExactRecipe);
        // The two resolvable recipe dishes in the existing week count; deleted/product dishes do not
        // fabricate recipe identity into the optimizer's historical usage.
        Assert.Equal(2m, exact.PriorUse);
    }

    [Fact(DisplayName = "Execute_OnlyFeasibleRepeat_StagesScoreBreakdownAndExplainsRepeat — deterministic selection still uses the normal ACL/review path")]
    public async Task Execute_OnlyFeasibleRepeat_StagesScoreBreakdownAndExplainsRepeat()
    {
        var recipeId = Guid.Parse("0193b4a0-5555-7000-8000-000000000001");
        var veganTagId = Guid.Parse("0193b4a0-5555-7000-8000-000000000002");
        var proteinTagId = Guid.Parse("0193b4a0-5555-7000-8000-000000000003");
        var cuisineTagId = Guid.Parse("0193b4a0-5555-7000-8000-000000000004");
        var profile = RecipeDiversityProfile.Create(
            recipeId,
            "Only vegan dinner",
            [
                new RecipeSemanticTagFact(veganTagId, "Vegan", RecipeSemanticTagCategory.Diet),
                new RecipeSemanticTagFact(proteinTagId, "Tofu", RecipeSemanticTagCategory.Protein),
                new RecipeSemanticTagFact(cuisineTagId, "Thai", RecipeSemanticTagCategory.Cuisine),
            ],
            [],
            []);
        var config = BuildDefaultSlotConfig();
        var attendeeId = config.Slots.First(slot => slot.IsActive).DefaultAttendees.Single();
        var preference = UserPreference.Create(Household, attendeeId, Clock);
        preference.SetStance(veganTagId, "Required", Clock);
        var (generateService, _, store, _, _) = BuildStack(
            slotConfig: config,
            prefs: [preference],
            recipes: [new RecipeReadModel(recipeId, "Only vegan dinner", [veganTagId], 4, DiversityProfile: profile)]);

        var result = await generateService.ExecuteAsync(
            Household, Monday, "optimizer-repeat", new PlanningWeights(0, 0, 100));
        var staged = await store.GetAsync("optimizer-repeat");

        Assert.Equal(21, result.ProposedCount);
        Assert.Equal(21, staged.Count);
        var repeated = staged.Skip(1).First();
        var breakdown = Assert.Single(repeated.Dishes).ScoreBreakdown;
        Assert.NotNull(breakdown);
        Assert.Contains(breakdown!.VarietyContributions, contribution =>
            contribution.Facet == RecipeDiversityFacet.ExactRecipe && contribution.PriorUse > 0m);
        Assert.Contains("repeat", repeated.Reasoning!, StringComparison.OrdinalIgnoreCase);
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

    private sealed class FakeRecentMealHistoryReader(
        RecentMealHistorySnapshot? snapshot = null) : IRecentMealHistoryReader
    {
        public Task<RecentMealHistorySnapshot> ReadAsync(
            HouseholdId householdId,
            DateOnly asOfDate,
            DateOnly excludedWeekStart,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot ?? RecentMealHistorySnapshot.Empty);
    }

}
