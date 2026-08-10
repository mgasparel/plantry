using Plantry.Planning.Application;
using Plantry.Planning.Domain;

namespace Plantry.Tests.Unit.MealPlanning.Application;

public sealed class DeterministicWeekMealOptimizerTests
{
    private static readonly DateOnly Monday = new(2026, 6, 15);
    private static readonly Guid Vegan = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Tofu = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Legumes = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Thai = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid Japanese = Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly Guid Mexican = Guid.Parse("10000000-0000-0000-0000-000000000006");

    [Fact]
    public void Production_Sized_Corpus_Completes_Within_Bounded_Search_And_Is_Deterministic()
    {
        var candidates = Enumerable.Range(0, 50)
            .Select(index => Candidate($"21000000-0000-0000-0000-{index + 1:000000000000}", $"Recipe {index}",
                index % 2 == 0 ? Tofu : Legumes, index % 3 == 0 ? Thai : Japanese))
            .ToList();
        var contexts = Contexts(21, candidates);
        var firstNodes = new List<int>();
        var first = DeterministicWeekMealOptimizer.Select(
            contexts, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 0, 100),
            onSearchNode: node => firstNodes.Add(node));
        var secondNodes = new List<int>();
        var second = DeterministicWeekMealOptimizer.Select(
            contexts.Reverse().ToList(), [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 0, 100),
            onSearchNode: node => secondNodes.Add(node));

        Assert.Equal(DeterministicWeekMealOptimizer.MaxSearchNodes, firstNodes.Max());
        Assert.Equal(firstNodes, secondNodes);
        Assert.Equal(first.SelectMany(meal => meal.Dishes).Select(dish => dish.RecipeId),
            second.SelectMany(meal => meal.Dishes).Select(dish => dish.RecipeId));
        Assert.Equal(
            first.SelectMany(meal => meal.Dishes).Select(dish => dish.ScoreBreakdown!.ObjectiveScore),
            second.SelectMany(meal => meal.Dishes).Select(dish => dish.ScoreBreakdown!.ObjectiveScore));
        Assert.Equal(contexts.Count, first.Count);
        Assert.All(first, meal => Assert.Single(meal.Dishes));
    }

    [Fact]
    public void Variety_Dominant_Vegan_Corpus_Increases_Confirmed_Protein_And_Cuisine_Diversity()
    {
        var tofuThai = Candidate("20000000-0000-0000-0000-000000000001", "Tofu curry", Tofu, Thai);
        var tofuJapanese = Candidate("20000000-0000-0000-0000-000000000002", "Tofu noodles", Tofu, Japanese);
        var legumesThai = Candidate("20000000-0000-0000-0000-000000000003", "Chickpea curry", Legumes, Thai);
        var legumesMexican = Candidate("20000000-0000-0000-0000-000000000004", "Bean tacos", Legumes, Mexican);
        var candidates = new[] { tofuThai, tofuJapanese, legumesThai, legumesMexican };
        var requiredVegan = new GenerationConstraints(
            [], [new AttendeeHardStances(Guid.NewGuid(), [Vegan], [])], new Dictionary<Guid, float>());
        var contexts = Contexts(4, candidates, requiredVegan);

        var lowVariety = DeterministicWeekMealOptimizer.Select(
            contexts, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 100, 0));
        var highVariety = DeterministicWeekMealOptimizer.Select(
            contexts, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 0, 100));

        Assert.All(highVariety, proposal =>
        {
            var recipeId = Assert.Single(proposal.Dishes).RecipeId;
            Assert.Contains(candidates, candidate => candidate.RecipeId == recipeId && candidate.TagIds.Contains(Vegan));
        });
        Assert.True(DistinctFacetCount(highVariety, candidates, RecipeDiversityFacet.Protein)
                    >= DistinctFacetCount(lowVariety, candidates, RecipeDiversityFacet.Protein));
        Assert.True(DistinctFacetCount(highVariety, candidates, RecipeDiversityFacet.Cuisine)
                    >= DistinctFacetCount(lowVariety, candidates, RecipeDiversityFacet.Cuisine));
        Assert.True(MaxRecipeConcentration(highVariety) <= MaxRecipeConcentration(lowVariety));
        Assert.True(DistinctFacetCount(highVariety, candidates, RecipeDiversityFacet.Protein) > 1);
        Assert.True(DistinctFacetCount(highVariety, candidates, RecipeDiversityFacet.Cuisine) > 1);
    }

    [Fact]
    public void Cost_And_Waste_Dominant_Objectives_Select_Only_Evidence_Backed_Winners()
    {
        var expensive = Candidate(
            "20000000-0000-0000-0000-000000000010", "Expensive", Tofu, Thai,
            cost: 12m, costCompleteness: CandidateCostCompleteness.Complete, expiring: false);
        var cheap = Candidate(
            "20000000-0000-0000-0000-000000000011", "Cheap", Legumes, Japanese,
            cost: 2m, costCompleteness: CandidateCostCompleteness.Complete, expiring: false);
        var unknown = Candidate(
            "20000000-0000-0000-0000-000000000012", "Unknown", Legumes, Mexican,
            cost: null, costCompleteness: CandidateCostCompleteness.Unknown, expiring: true);
        var contexts = Contexts(1, [expensive, cheap, unknown]);

        var costChoice = Assert.Single(DeterministicWeekMealOptimizer.Select(
            contexts, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 100, 0)));
        var wasteChoice = Assert.Single(DeterministicWeekMealOptimizer.Select(
            contexts, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(100, 0, 0)));

        Assert.Equal(cheap.RecipeId, Assert.Single(costChoice.Dishes).RecipeId);
        Assert.Equal(unknown.RecipeId, Assert.Single(wasteChoice.Dishes).RecipeId);
        var score = Assert.Single(costChoice.Dishes).ScoreBreakdown!;
        Assert.Equal(1m, score.CostScore);
        Assert.Equal(1m, score.CostContribution);
        Assert.Equal(0m, score.WasteContribution);
    }

    [Fact]
    public void Recent_History_Is_A_Soft_Marginal_Variety_Penalty()
    {
        var recentTofu = Candidate("20000000-0000-0000-0000-000000000020", "Recent tofu", Tofu, Thai);
        var novelBeans = Candidate("20000000-0000-0000-0000-000000000021", "Novel beans", Legumes, Japanese);
        var history = new RecentMealHistorySnapshot(
        [
            new RecentRecipeHistory(
                recentTofu.RecipeId,
                recentTofu.Name,
                false,
                [new RecentMealOccurrence(Monday.AddDays(-1), RecentMealOccurrenceSource.CookEvent, 1m)],
                [
                    new RecentRecipeFacet(Vegan, "Vegan", nameof(RecipeSemanticTagCategory.Diet)),
                    new RecentRecipeFacet(Tofu, "Tofu", nameof(RecipeSemanticTagCategory.Protein)),
                    new RecentRecipeFacet(Thai, "Thai", nameof(RecipeSemanticTagCategory.Cuisine)),
                ])
        ]);

        var proposal = Assert.Single(DeterministicWeekMealOptimizer.Select(
            Contexts(1, [recentTofu, novelBeans]), [], history, new PlanningWeights(0, 0, 100)));

        Assert.Equal(novelBeans.RecipeId, Assert.Single(proposal.Dishes).RecipeId);
    }

    [Fact]
    public void Repetition_Remains_Allowed_And_Records_The_Marginal_Repeat_Evidence()
    {
        var onlyChoice = Candidate("20000000-0000-0000-0000-000000000030", "Only dinner", Tofu, Thai);

        var selected = DeterministicWeekMealOptimizer.Select(
            Contexts(2, [onlyChoice]), [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 0, 100));

        Assert.Equal(2, selected.Count);
        Assert.All(selected, proposal => Assert.Equal(onlyChoice.RecipeId, Assert.Single(proposal.Dishes).RecipeId));
        var repeated = Assert.Single(selected[1].Dishes).ScoreBreakdown!;
        Assert.Contains(repeated.VarietyContributions, contribution =>
            contribution.Facet == RecipeDiversityFacet.ExactRecipe && contribution.PriorUse == 1m);
    }

    [Fact]
    public void Exact_Week_Search_Beats_The_Faithful_Legacy_128_State_Beam_And_Matches_The_Exhaustive_Oracle()
    {
        // Every cell has five candidates, so production's least-flexible ordering retains date order.
        // The final two cells contain four copies of A: the old 128-state beam prunes the lower prefix
        // that preserves A, while the exhaustive oracle shows that preserving it wins the full week.
        var forcedA = ExactOnlyCandidate("20000000-0000-0000-0000-000000000031", "A");
        var b = ExactOnlyCandidate("20000000-0000-0000-0000-000000000032", "B");
        var c = ExactOnlyCandidate("20000000-0000-0000-0000-000000000033", "C");
        var d = ExactOnlyCandidate("20000000-0000-0000-0000-000000000034", "D");
        var e = ExactOnlyCandidate("20000000-0000-0000-0000-000000000035", "E");
        var allCandidates = new[] { forcedA, b, c, d, e };
        var contexts = Enumerable.Range(0, 5)
            .Select(index => Context(index, allCandidates))
            .Concat([
                Context(5, [forcedA, forcedA, forcedA, forcedA, forcedA]),
                Context(6, [forcedA, forcedA, forcedA, forcedA, forcedA]),
                Context(7, [forcedA, forcedA, forcedA, forcedA, forcedA]),
            ])
            .ToList();

        var legacy = LegacyBeam(contexts);
        var oracle = ExhaustiveOracle(contexts);
        var selected = DeterministicWeekMealOptimizer.Select(
            contexts, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 0, 100));

        var selectedIds = selected.Select(proposal => proposal.Dishes.Single().RecipeId).ToList();
        Assert.True(legacy.Objective < oracle.Objective);
        Assert.Equal(oracle.Path, selectedIds);
        Assert.Equal(oracle.Objective, selected.Sum(proposal => proposal.Dishes.Single().ScoreBreakdown!.WeightedScore));
    }

    [Fact]
    public void Rounded_Suffix_Bounds_Preserve_An_Objective_Tie_For_The_Higher_Rating_Plan()
    {
        // These confirmed facets produce repeating thirds; each per-cell objective is rounded before
        // accumulation. The higher-rated B path must survive the objective tie.
        var a = Candidate("20000000-0000-0000-0000-000000000051", "A", Tofu, Thai, rating: 1m);
        var b = Candidate("20000000-0000-0000-0000-000000000052", "B", Tofu, Thai, rating: 5m);
        var selected = DeterministicWeekMealOptimizer.Select(
            Contexts(3, [a, b]), [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 0, 100));

        Assert.Equal(2, selected.Count(proposal => proposal.Dishes.Single().RecipeId == b.RecipeId));
    }

    [Fact]
    public void Eight_Cells_With_Repeated_Facets_Match_Independent_Exhaustive_Oracle()
    {
        var candidates = Enumerable.Range(1, 5).Select(index => MixedCandidate(index, index % 2)).ToList();
        var contexts = Contexts(6, candidates);
        var oracle = MixedExhaustiveOracle(contexts, new PlanningWeights(35, 40, 25));
        var selected = DeterministicWeekMealOptimizer.Select(
            contexts, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(35, 40, 25));

        var path = selected.Select(proposal => proposal.Dishes.Single().RecipeId).ToList();
        Assert.Equal(oracle.Path, path);
        Assert.Equal(oracle.Objective,
            selected.Sum(proposal => proposal.Dishes.Single().ScoreBreakdown!.WeightedScore));
        var reversed = DeterministicWeekMealOptimizer.Select(
            contexts.AsEnumerable().Reverse().ToList(), [], RecentMealHistorySnapshot.Empty,
            new PlanningWeights(35, 40, 25));
        Assert.Equal(path, reversed.Select(proposal => proposal.Dishes.Single().RecipeId));

        var candidateReversed = contexts
            .Select(context => context with { CandidateRecipes = context.CandidateRecipes.Reverse().ToList() })
            .ToList();
        var reordered = DeterministicWeekMealOptimizer.Select(
            candidateReversed, [], RecentMealHistorySnapshot.Empty, new PlanningWeights(35, 40, 25));
        Assert.Equal(path, reordered.Select(proposal => proposal.Dishes.Single().RecipeId));
    }

    [Fact]
    public void Objective_Wins_Before_Ratings_And_Preferences_Then_Stable_Identity_Breaks_True_Ties()
    {
        var preferredTag = Guid.Parse("10000000-0000-0000-0000-000000000010");
        var objectiveWinner = Candidate(
            "20000000-0000-0000-0000-000000000040", "Objective winner", Tofu, Thai,
            cost: 1m, costCompleteness: CandidateCostCompleteness.Complete, rating: 1m);
        var likedButCostly = Candidate(
            "20000000-0000-0000-0000-000000000041", "Liked", Legumes, Japanese,
            cost: 10m, costCompleteness: CandidateCostCompleteness.Complete, rating: 5m,
            additionalTags: [preferredTag]);
        var preferredConstraints = new GenerationConstraints(
            [], [], new Dictionary<Guid, float> { [preferredTag] = 1f });

        var objectiveChoice = Assert.Single(DeterministicWeekMealOptimizer.Select(
            Contexts(1, [objectiveWinner, likedButCostly], preferredConstraints), [],
            RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 100, 0)));
        Assert.Equal(objectiveWinner.RecipeId, Assert.Single(objectiveChoice.Dishes).RecipeId);

        var equalCostA = Candidate("20000000-0000-0000-0000-000000000042", "A", Tofu, Thai, cost: 2m,
            costCompleteness: CandidateCostCompleteness.Complete);
        var equalCostB = Candidate("20000000-0000-0000-0000-000000000043", "B", Legumes, Japanese, cost: 2m,
            costCompleteness: CandidateCostCompleteness.Complete);
        var first = DeterministicWeekMealOptimizer.Select(
            Contexts(1, [equalCostB, equalCostA]), [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 100, 0));
        var second = DeterministicWeekMealOptimizer.Select(
            Contexts(1, [equalCostA, equalCostB]), [], RecentMealHistorySnapshot.Empty, new PlanningWeights(0, 100, 0));

        Assert.Equal(equalCostA.RecipeId, Assert.Single(first).Dishes.Single().RecipeId);
        var firstScore = Assert.Single(first).Dishes.Single().ScoreBreakdown!;
        var secondScore = Assert.Single(second).Dishes.Single().ScoreBreakdown!;
        Assert.Equal(firstScore.WeightedScore, secondScore.WeightedScore);
        Assert.Equal(firstScore.WasteScore, secondScore.WasteScore);
        Assert.Equal(firstScore.CostScore, secondScore.CostScore);
        Assert.Equal(firstScore.VarietyScore, secondScore.VarietyScore);
        Assert.Equal(
            firstScore.VarietyContributions.Select(ContributionFingerprint),
            secondScore.VarietyContributions.Select(ContributionFingerprint));
    }

    private static IReadOnlyList<PlannerMealSlotContext> Contexts(
        int count,
        IReadOnlyList<CandidateRecipe> candidates,
        GenerationConstraints? constraints = null) =>
        Enumerable.Range(0, count)
            .Select(index => Context(index, candidates, constraints))
            .ToList();

    private static PlannerMealSlotContext Context(
        int index,
        IReadOnlyList<CandidateRecipe> candidates,
        GenerationConstraints? constraints = null) => new(
        Monday.AddDays(index),
        MealSlotId.From(Guid.Parse($"30000000-0000-0000-0000-{index + 1:000000000000}")),
        "Dinner",
        [],
        constraints ?? GenerationConstraints.Empty,
        candidates);

    private static CandidateRecipe ExactOnlyCandidate(string id, string name)
    {
        var recipeId = Guid.Parse(id);
        return new CandidateRecipe(recipeId, name, [], 4, null, HouseholdAvgRating: 5m,
            DiversityProfile: new RecipeDiversityProfile(
                [Value($"recipe:{recipeId:N}", name, null)], [], [], [], []));
    }

    private static CandidateRecipe MixedCandidate(int index, int facetGroup)
    {
        var recipeId = Guid.Parse($"60000000-0000-0000-0000-{index:000000000000}");
        var protein = Guid.Parse($"61000000-0000-0000-0000-{facetGroup + 1:000000000000}");
        var cuisine = Guid.Parse($"62000000-0000-0000-0000-{facetGroup + 1:000000000000}");
        return Candidate(recipeId.ToString(), $"Mixed {index}", protein, cuisine,
            cost: (index % 7) + 1m,
            costCompleteness: CandidateCostCompleteness.Complete,
            expiring: index % 11 == 0,
            rating: (index % 5) + 1m);
    }

    private static CandidateRecipe UniqueFacetCandidate(int index)
    {
        var recipeId = Guid.Parse($"50000000-0000-0000-0000-{index:000000000000}");
        Guid Tag(int category) => Guid.Parse($"5{category}000000-0000-0000-0000-{index:000000000000}");
        RecipeDiversityFacetValue Facet(string category, int ordinal) =>
            Value($"tag:{Tag(ordinal):N}", $"{category} {index}", Tag(ordinal));

        return new CandidateRecipe(recipeId, $"Unique {index}", [], 4, null, HouseholdAvgRating: 5m,
            DiversityProfile: new RecipeDiversityProfile(
                [Value($"recipe:{recipeId:N}", $"Unique {index}", null)],
                [Facet("Diet", 1)], [Facet("Protein", 2)], [Facet("Cuisine", 3)], [Facet("Flavor", 4)]));
    }

    private static LegacyPlan LegacyBeam(IReadOnlyList<PlannerMealSlotContext> contexts)
    {
        var ordered = Ordered(contexts);
        IReadOnlyList<LegacyPlan> states = [new LegacyPlan([], 0m, new Dictionary<Guid, int>())];
        foreach (var context in ordered)
        {
            states = states
                .SelectMany(state => context.CandidateRecipes.Select(candidate => Extend(state, candidate)))
                .OrderByDescending(state => state.Objective)
                .ThenBy(state => Identity(state.Path), StringComparer.Ordinal)
                .Take(128)
                .ToList();
        }
        return states.Aggregate(Better);
    }

    private static LegacyPlan MixedExhaustiveOracle(IReadOnlyList<PlannerMealSlotContext> contexts, PlanningWeights weights)
    {
        // Independent exhaustive reference mirroring the production normalization, per-facet marginal
        // novelty, and six-decimal per-cell rounding. It intentionally does not call optimizer helpers.
        var completeCosts = contexts.SelectMany(c => c.CandidateRecipes)
            .Where(c => c.CostCompleteness == CandidateCostCompleteness.Complete && c.CostPerServing.HasValue)
            .Select(c => c.CostPerServing!.Value).ToList();
        var minCost = completeCosts.Min();
        var maxCost = completeCosts.Max();
        LegacyPlan? best = null;
        void Visit(int index, IReadOnlyList<Guid> path, decimal objective,
            Dictionary<(RecipeDiversityFacet, string), decimal> usage)
        {
            if (index == contexts.Count)
            {
                var candidate = new LegacyPlan(path, objective, new Dictionary<Guid, int>());
                best = best is null ? candidate : Better(best, candidate);
                return;
            }
            foreach (var recipe in contexts[index].CandidateRecipes)
            {
                var waste = recipe.HasContributingExpiringStock == true ? 1m : 0m;
                var cost = recipe.CostCompleteness == CandidateCostCompleteness.Complete && recipe.CostPerServing.HasValue
                    ? (minCost == maxCost ? 1m : (maxCost - recipe.CostPerServing.Value) / (maxCost - minCost)) : 0m;
                var variety = 0m;
                foreach (var (facet, weight) in new[] {
                    (RecipeDiversityFacet.ExactRecipe, .30m), (RecipeDiversityFacet.Protein, .25m),
                    (RecipeDiversityFacet.Cuisine, .25m), (RecipeDiversityFacet.Diet, .10m),
                    (RecipeDiversityFacet.Flavor, .10m) })
                {
                    var values = facet == RecipeDiversityFacet.ExactRecipe
                        ? recipe.DiversityProfile!.ExactRecipe
                        : recipe.DiversityProfile!.Values(facet);
                    variety += weight * (values.Count == 0
                        ? 0.50m
                        : values.Average(v => 1m / (1m + usage.GetValueOrDefault((facet, v.Key)))));
                }
                var cellScore = decimal.Round(weights.Waste / 100m * waste + weights.Cost / 100m * cost
                    + weights.Variety / 100m * variety, DeterministicWeekMealOptimizer.ObjectiveScorePrecision,
                    MidpointRounding.AwayFromZero);
                var next = usage.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var (facet, _) in new[] { (RecipeDiversityFacet.ExactRecipe, 0m), (RecipeDiversityFacet.Protein, 0m), (RecipeDiversityFacet.Cuisine, 0m), (RecipeDiversityFacet.Diet, 0m), (RecipeDiversityFacet.Flavor, 0m) })
                    foreach (var value in (facet == RecipeDiversityFacet.ExactRecipe ? recipe.DiversityProfile!.ExactRecipe : recipe.DiversityProfile!.Values(facet)))
                        next[(facet, value.Key)] = next.GetValueOrDefault((facet, value.Key)) + 1m;
                Visit(index + 1, path.Append(recipe.RecipeId).ToList(), objective + cellScore, next);
            }
        }
        Visit(0, [], 0m, new());
        return best!;
    }

    private static LegacyPlan ExhaustiveOracle(IReadOnlyList<PlannerMealSlotContext> contexts)
    {
        LegacyPlan? best = null;
        void Visit(int index, LegacyPlan state)
        {
            if (index == contexts.Count)
            {
                best = best is null ? state : Better(best, state);
                return;
            }
            foreach (var candidate in Ordered(contexts)[index].CandidateRecipes)
                Visit(index + 1, Extend(state, candidate));
        }
        Visit(0, new LegacyPlan([], 0m, new Dictionary<Guid, int>()));
        return best!;
    }

    private static IReadOnlyList<PlannerMealSlotContext> Ordered(IReadOnlyList<PlannerMealSlotContext> contexts) => contexts
        .OrderBy(context => context.CandidateRecipes.Count)
        .ThenBy(context => context.Date)
        .ThenBy(context => context.MealSlotId.Value)
        .ToList();

    private static LegacyPlan Extend(LegacyPlan state, CandidateRecipe candidate)
    {
        var seen = state.Usage.GetValueOrDefault(candidate.RecipeId);
        // Exact-only profiles have 0.35 neutral missing-facet variety plus exact-recipe novelty.
        var score = decimal.Round(0.35m + 0.30m / (1m + seen),
            DeterministicWeekMealOptimizer.ObjectiveScorePrecision, MidpointRounding.AwayFromZero);
        var usage = state.Usage.ToDictionary(pair => pair.Key, pair => pair.Value);
        usage[candidate.RecipeId] = seen + 1;
        return new LegacyPlan(state.Path.Append(candidate.RecipeId).ToList(), state.Objective + score, usage);
    }

    private static LegacyPlan Better(LegacyPlan left, LegacyPlan right) =>
        left.Objective != right.Objective
            ? left.Objective > right.Objective ? left : right
            : string.Compare(Identity(left.Path), Identity(right.Path), StringComparison.Ordinal) <= 0 ? left : right;

    private static string Identity(IReadOnlyList<Guid> path) => string.Join('|', path.Select(id => id.ToString("N")));

    private sealed record LegacyPlan(IReadOnlyList<Guid> Path, decimal Objective, IReadOnlyDictionary<Guid, int> Usage);

    private static CandidateRecipe Candidate(
        string id,
        string name,
        Guid protein,
        Guid cuisine,
        decimal? cost = null,
        CandidateCostCompleteness costCompleteness = CandidateCostCompleteness.Unknown,
        bool? expiring = null,
        decimal? rating = 5m,
        IReadOnlyList<Guid>? additionalTags = null)
    {
        var recipeId = Guid.Parse(id);
        var tagIds = new[] { Vegan, protein, cuisine }.Concat(additionalTags ?? []).ToList();
        return new CandidateRecipe(
            recipeId, name, tagIds, 4, cost,
            HouseholdAvgRating: rating,
            CostCompleteness: costCompleteness,
            HasContributingExpiringStock: expiring,
            DiversityProfile: Profile(recipeId, name, protein, cuisine));
    }

    private static RecipeDiversityProfile Profile(Guid recipeId, string name, Guid protein, Guid cuisine) => new(
        [Value($"recipe:{recipeId:N}", name, null)],
        [Value($"tag:{Vegan:N}", "Vegan", Vegan)],
        [Value($"tag:{protein:N}", protein == Tofu ? "Tofu" : "Legumes", protein)],
        [Value($"tag:{cuisine:N}", cuisine switch { var id when id == Thai => "Thai", var id when id == Japanese => "Japanese", _ => "Mexican" }, cuisine)],
        []);

    private static RecipeDiversityFacetValue Value(string key, string name, Guid? tagId) =>
        new(key, name, tagId, RecipeDiversityEvidenceSource.ConfirmedTag);

    private static int DistinctFacetCount(
        IReadOnlyList<ProposedMeal> proposals,
        IReadOnlyList<CandidateRecipe> candidates,
        RecipeDiversityFacet facet) =>
        proposals.Select(proposal => candidates.Single(candidate => candidate.RecipeId == proposal.Dishes.Single().RecipeId))
            .SelectMany(candidate => candidate.DiversityProfile!.Values(facet))
            .Select(value => value.Key)
            .Distinct()
            .Count();

    private static int MaxRecipeConcentration(IReadOnlyList<ProposedMeal> proposals) =>
        proposals.GroupBy(proposal => proposal.Dishes.Single().RecipeId).Max(group => group.Count());

    private static string ContributionFingerprint(RecipeFacetContribution contribution) =>
        $"{contribution.Facet}:{contribution.MarginalScore}:{contribution.PriorUse}:{contribution.Confidence}:{string.Join(',', contribution.MatchedValues)}";
}
