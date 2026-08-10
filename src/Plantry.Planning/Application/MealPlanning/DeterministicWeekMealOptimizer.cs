using Plantry.Planning.Domain;

namespace Plantry.Planning.Application;

/// <summary>
/// Selects one feasible recipe for every requested meal cell without delegating recipe identity to an
/// external model. The optimizer assembles the requested scope as one deterministic week: constrained
/// cells are filled first and each later choice sees the diversity already committed for this pass.
/// </summary>
public static class DeterministicWeekMealOptimizer
{
    /// <summary>The decimal precision at which two weighted objectives are considered equal.</summary>
    public const int ObjectiveScorePrecision = 6;

    /// <summary>
    /// Maximum recursive search nodes per selection. The exact search is valuable for small scopes, but
    /// its worst case is exponential. Once this budget is reached the deterministic greedy incumbent is
    /// retained, making runtime bounded without changing results for plans solved within the budget.
    /// </summary>
    public const int MaxSearchNodes = 10_000;

    private static readonly IReadOnlyList<(RecipeDiversityFacet Facet, decimal Weight)> VarietyFacets =
    [
        (RecipeDiversityFacet.ExactRecipe, 0.30m),
        (RecipeDiversityFacet.Protein, 0.25m),
        (RecipeDiversityFacet.Cuisine, 0.25m),
        (RecipeDiversityFacet.Diet, 0.10m),
        (RecipeDiversityFacet.Flavor, 0.10m),
    ];

    /// <summary>
    /// Produces server-owned proposals for the supplied cells. Required and Restricted stances are applied
    /// before any objective is calculated. A cell with no feasible candidate remains unfilled; callers keep
    /// ProposalAcl as the final trust-boundary validation before staging.
    /// </summary>
    public static IReadOnlyList<ProposedMeal> Select(
        IReadOnlyList<PlannerMealSlotContext> contexts,
        IReadOnlyList<PlannedMealSummary> alreadyPlanned,
        RecentMealHistorySnapshot recentHistory,
        PlanningWeights weights,
        CancellationToken ct = default,
        Action<int>? onSearchNode = null)
    {
        var cells = contexts
            .Select(context => new FeasibleCell(
                context,
                context.CandidateRecipes.Where(candidate => IsFeasible(candidate, context.Constraints)).ToList()))
            .Where(cell => cell.Candidates.Count > 0)
            // Least-flexible cells first protects the choices that cannot be deferred. Date/slot make the
            // assembly order stable when flexibility is equal; this is still a single shared week state.
            .OrderBy(cell => cell.Candidates.Count)
            .ThenBy(cell => cell.Context.Date)
            .ThenBy(cell => cell.Context.MealSlotId.Value)
            .ToList();

        if (cells.Count == 0) return [];

        var costScale = CostScale.From(cells.SelectMany(cell => cell.Candidates));
        var initialState = new OptimizerState(FacetUsage.From(alreadyPlanned, recentHistory), [], 0m, 0m, 0m);
        // A deterministic incumbent makes the admissible equal-objective/tie-break bounds useful on
        // production-sized scopes. It does not limit the search: every non-dominated branch remains exact.
        OptimizerState? selectedWeek = BuildGreedyIncumbent(initialState);
        var dominance = new Dictionary<(int CellIndex, string Usage), OptimizerState>();
        var searchNodes = 0;
        Search(cellIndex: 0, initialState);

        // At least one feasible cell exists, so the complete exact search must produce a week.
        var finalWeek = selectedWeek ?? throw new InvalidOperationException("Exact week search produced no feasible plan.");
        // The optimizer's assembly order is deliberately independent of the presentation/staging order.
        return finalWeek.Choices
            .Select(choice => new ProposedMeal(
                choice.Context.Date,
                choice.Context.MealSlotId,
                choice.Context.EffectiveAttendees,
                [new ProposedDish(
                    choice.Score.Candidate.RecipeId,
                    choice.Score.Candidate.DefaultServings,
                    Ordinal: 1,
                    choice.Score.Breakdown)],
                Reasoning: null))
            .OrderBy(proposal => proposal.Date)
            .ThenBy(proposal => proposal.MealSlotId.Value)
            .ToList();

        void Search(int cellIndex, OptimizerState state)
        {
            ct.ThrowIfCancellationRequested();
            if (++searchNodes > MaxSearchNodes) return;
            onSearchNode?.Invoke(searchNodes);
            var dominanceKey = (cellIndex, state.Usage.Fingerprint());
            if (dominance.TryGetValue(dominanceKey, out var prior) && !IsBetter(state, prior)) return;
            dominance[dominanceKey] = state;
            if (cellIndex == cells.Count)
            {
                if (selectedWeek is null || IsBetter(state, selectedWeek))
                    selectedWeek = state;
                return;
            }

            if (selectedWeek is not null && CannotBeatIncumbent(cellIndex, state, selectedWeek, cells))
                return;

            var cell = cells[cellIndex];
            foreach (var candidate in cell.Candidates
                .Select(candidate => Score(candidate, cell.Context.Constraints, state.Usage, costScale, weights))
                .OrderByDescending(score => score.RoundedObjective)
                .ThenByDescending(score => score.TieBreakSignals.PreferredTagSignal)
                .ThenByDescending(score => score.TieBreakSignals.RatingSignal)
                .ThenBy(score => score.Candidate.RecipeId))
            {
                var nextUsage = state.Usage.Clone();
                nextUsage.Add(candidate.Candidate, 1m);
                Search(
                    cellIndex + 1,
                    new OptimizerState(
                        nextUsage,
                        state.Choices.Append(new SelectedChoice(cell.Context, candidate)).ToList(),
                        state.Objective + candidate.RoundedObjective,
                        state.PreferredTags + candidate.TieBreakSignals.PreferredTagSignal,
                        state.Rating + candidate.TieBreakSignals.RatingSignal));
            }
        }

        OptimizerState BuildGreedyIncumbent(OptimizerState state)
        {
            for (var index = 0; index < cells.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var cell = cells[index];
                var score = cell.Candidates
                    .Select(candidate => Score(candidate, cell.Context.Constraints, state.Usage, costScale, weights))
                    .OrderByDescending(candidate => candidate.RoundedObjective)
                    .ThenByDescending(candidate => candidate.TieBreakSignals.PreferredTagSignal)
                    .ThenByDescending(candidate => candidate.TieBreakSignals.RatingSignal)
                    .ThenBy(candidate => candidate.Candidate.RecipeId)
                    .First();
                var usage = state.Usage.Clone();
                usage.Add(score.Candidate, 1m);
                state = new OptimizerState(
                    usage,
                    state.Choices.Append(new SelectedChoice(cell.Context, score)).ToList(),
                    state.Objective + score.RoundedObjective,
                    state.PreferredTags + score.TieBreakSignals.PreferredTagSignal,
                    state.Rating + score.TieBreakSignals.RatingSignal);
            }
            return state;
        }

        bool CannotBeatIncumbent(int cellIndex, OptimizerState state, OptimizerState incumbent, IReadOnlyList<FeasibleCell> remainingCells)
        {
            var bound = BuildStateAwareBound(remainingCells, cellIndex, state.Usage, costScale, weights);
            var objective = state.Objective + bound.Objective;
            if (objective != incumbent.Objective) return objective < incumbent.Objective;

            // Preferred and rating totals are usage-independent signals, but their maxima are not
            // guaranteed by the state-aware objective bound. Do not prune objective ties without a
            // separately proven bound for every preceding tie-break signal.
            return false;
        }

    }

    private static SuffixBound BuildStateAwareBound(
        IReadOnlyList<FeasibleCell> cells,
        int start,
        FacetUsage usage,
        CostScale costScale,
        PlanningWeights weights)
    {
        // This is an admissible completion bound: each remaining cell independently receives its
        // best possible waste, cost, and novelty score. It deliberately ignores collisions and usage
        // growth, so it can overestimate but never underestimates any completion. Unlike a node cutoff,
        // pruning with this bound cannot discard an objectively better mixed-objective plan.
        var bound = new SuffixBound(0m, 0m, 0m);
        for (var index = start; index < cells.Count; index++)
        {
            var best = cells[index].Candidates
                .Select(candidate => Score(candidate, cells[index].Context.Constraints, usage, costScale, weights))
                .OrderByDescending(score => score.RoundedObjective)
                .ThenByDescending(score => score.TieBreakSignals.PreferredTagSignal)
                .ThenByDescending(score => score.TieBreakSignals.RatingSignal)
                .First();
            bound = new SuffixBound(
                bound.Objective + best.RoundedObjective,
                bound.PreferredTags + best.TieBreakSignals.PreferredTagSignal,
                bound.Rating + best.TieBreakSignals.RatingSignal);
        }
        return bound;
    }

    private static bool IsBetter(OptimizerState candidate, OptimizerState incumbent)
    {
        var candidateObjective = decimal.Round(candidate.Objective, ObjectiveScorePrecision, MidpointRounding.AwayFromZero);
        var incumbentObjective = decimal.Round(incumbent.Objective, ObjectiveScorePrecision, MidpointRounding.AwayFromZero);
        if (candidateObjective != incumbentObjective) return candidateObjective > incumbentObjective;
        if (candidate.PreferredTags != incumbent.PreferredTags) return candidate.PreferredTags > incumbent.PreferredTags;
        if (candidate.Rating != incumbent.Rating) return candidate.Rating > incumbent.Rating;
        return string.Compare(candidate.IdentityPath, incumbent.IdentityPath, StringComparison.Ordinal) < 0;
    }

    private static bool IsFeasible(CandidateRecipe candidate, GenerationConstraints constraints) =>
        !candidate.TagIds.Any(constraints.RestrictedTagIds.Contains)
        && constraints.AttendeeStances.All(attendee =>
            attendee.RequiredTagIds.All(candidate.TagIds.Contains));

    private static ScoredCandidate Score(
        CandidateRecipe candidate,
        GenerationConstraints constraints,
        FacetUsage usage,
        CostScale costScale,
        PlanningWeights weights)
    {
        var wasteScore = candidate.HasContributingExpiringStock == true ? 1m : 0m;
        var costScore = costScale.Score(candidate);
        var facetContributions = VarietyFacets
            .Select(item => ScoreFacet(candidate, item.Facet, usage))
            .ToList();
        var varietyScore = VarietyFacets
            .Zip(facetContributions, (weight, contribution) => weight.Weight * contribution.MarginalScore)
            .Sum();

        var wasteContribution = weights.Waste / 100m * wasteScore;
        var costContribution = weights.Cost / 100m * costScore;
        var varietyContribution = weights.Variety / 100m * varietyScore;
        var objective = wasteContribution + costContribution + varietyContribution;
        var tieBreakSignals = new RecipeTieBreakSignals(
            PreferredTagSignal: PreferredTagSignal(candidate, constraints),
            RatingSignal: RatingSignal(candidate),
            CostEvidenceRank: candidate.CostCompleteness switch
            {
                CandidateCostCompleteness.Complete => 2,
                CandidateCostCompleteness.Partial => 1,
                _ => 0,
            },
            WasteEvidenceRank: candidate.HasContributingExpiringStock switch
            {
                true => 2,
                false => 1,
                null => 0,
            });

        return new ScoredCandidate(
            candidate,
            decimal.Round(objective, ObjectiveScorePrecision, MidpointRounding.AwayFromZero),
            new RecipeScoreBreakdown(
                candidate.RecipeId,
                decimal.Round(objective, ObjectiveScorePrecision, MidpointRounding.AwayFromZero),
                wasteScore,
                costScore,
                varietyScore,
                wasteContribution,
                costContribution,
                varietyContribution,
                facetContributions,
                tieBreakSignals),
            tieBreakSignals);
    }

    private static RecipeFacetContribution ScoreFacet(
        CandidateRecipe candidate,
        RecipeDiversityFacet facet,
        FacetUsage usage)
    {
        var values = Values(candidate, facet);
        var confidence = Confidence(candidate, facet, values);
        if (values.Count == 0)
        {
            // Missing semantic metadata is valid but cannot manufacture novelty. It contributes an explicit
            // neutral score rather than being silently treated as a distinct protein/cuisine.
            return new RecipeFacetContribution(facet, 0.50m, 0m, confidence, []);
        }

        var priorUse = values.Select(value => usage.Get(facet, value.Key)).Average();
        var observedNovelty = values.Select(value => 1m / (1m + usage.Get(facet, value.Key))).Average();
        var marginal = confidence == RecipeDiversityConfidence.Fallback
            ? 0.50m + ((observedNovelty - 0.50m) * 0.75m)
            : observedNovelty;

        return new RecipeFacetContribution(
            facet,
            marginal,
            priorUse,
            confidence,
            values.Select(value => value.DisplayName).OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<RecipeDiversityFacetValue> Values(
        CandidateRecipe candidate,
        RecipeDiversityFacet facet)
    {
        if (facet == RecipeDiversityFacet.ExactRecipe)
            return candidate.DiversityProfile?.ExactRecipe is { Count: > 0 } exact
                ? exact
                : [new RecipeDiversityFacetValue(
                    $"recipe:{candidate.RecipeId:N}", candidate.Name, null,
                    RecipeDiversityEvidenceSource.ConfirmedRecipeFact)];

        return candidate.DiversityProfile?.Values(facet) ?? [];
    }

    private static RecipeDiversityConfidence Confidence(
        CandidateRecipe candidate,
        RecipeDiversityFacet facet,
        IReadOnlyList<RecipeDiversityFacetValue> values) =>
        facet == RecipeDiversityFacet.ExactRecipe
            ? RecipeDiversityConfidence.Confirmed
            : values.Count == 0
                ? RecipeDiversityConfidence.Missing
                : candidate.DiversityProfile?.Confidence(facet) ?? RecipeDiversityConfidence.Missing;

    private static decimal PreferredTagSignal(CandidateRecipe candidate, GenerationConstraints constraints) =>
        candidate.TagIds
            .Where(constraints.PreferredTagWeights.ContainsKey)
            .Select(tagId => (decimal)constraints.PreferredTagWeights[tagId])
            .DefaultIfEmpty(0m)
            .Sum();

    private static decimal RatingSignal(CandidateRecipe candidate)
    {
        if (candidate.AttendeeStars is { Count: > 0 })
            return candidate.AttendeeStars.Values.Select(stars => (decimal)stars).Average();
        return candidate.HouseholdAvgRating ?? 0m;
    }

    private sealed record FeasibleCell(PlannerMealSlotContext Context, IReadOnlyList<CandidateRecipe> Candidates);

    private sealed record SuffixBound(decimal Objective, decimal PreferredTags, decimal Rating);

    private sealed record ScoredCandidate(
        CandidateRecipe Candidate,
        decimal RoundedObjective,
        RecipeScoreBreakdown Breakdown,
        RecipeTieBreakSignals TieBreakSignals);

    private sealed record SelectedChoice(PlannerMealSlotContext Context, ScoredCandidate Score);

    private sealed record OptimizerState(
        FacetUsage Usage,
        IReadOnlyList<SelectedChoice> Choices,
        decimal Objective,
        decimal PreferredTags,
        decimal Rating)
    {
        public string IdentityPath => string.Join('|', Choices.Select(choice => choice.Score.Candidate.RecipeId.ToString("N")));
    }

    private sealed class CostScale(decimal? min, decimal? max)
    {
        public static CostScale From(IEnumerable<CandidateRecipe> candidates)
        {
            var complete = candidates
                .Where(candidate => candidate.CostCompleteness == CandidateCostCompleteness.Complete
                    && candidate.CostPerServing.HasValue)
                .Select(candidate => candidate.CostPerServing!.Value)
                .ToList();
            return complete.Count == 0 ? new CostScale(null, null) : new CostScale(complete.Min(), complete.Max());
        }

        public decimal Score(CandidateRecipe candidate)
        {
            // Unresolved/partial values do not become an invented low price. They contribute no positive
            // cost objective until a complete comparison exists.
            if (min is null || max is null || candidate.CostCompleteness != CandidateCostCompleteness.Complete
                || !candidate.CostPerServing.HasValue)
                return 0m;
            if (min == max) return 1m;
            return (max.Value - candidate.CostPerServing.Value) / (max.Value - min.Value);
        }
    }

    private sealed class FacetUsage
    {
        private readonly Dictionary<(RecipeDiversityFacet Facet, string Key), decimal> _usage = [];

        public static FacetUsage From(
            IReadOnlyList<PlannedMealSummary> alreadyPlanned,
            RecentMealHistorySnapshot recentHistory)
        {
            var usage = new FacetUsage();
            foreach (var recipe in alreadyPlanned.SelectMany(meal => meal.RecipeChoices ?? []))
                usage.Add(recipe.RecipeId, recipe.DiversityProfile, 1m);

            foreach (var recipe in recentHistory.Recipes)
            {
                var amount = recipe.RecencyScore;
                if (amount <= 0m) continue;

                usage.Add(RecipeDiversityFacet.ExactRecipe, $"recipe:{recipe.RecipeId:N}", amount);
                foreach (var facet in recipe.Facets)
                {
                    if (!TryMapFacet(facet.Category, out var mapped)) continue;
                    usage.Add(mapped, $"tag:{facet.TagId:N}", amount);
                }
            }

            return usage;
        }

        public string Fingerprint() => string.Join("|", _usage.OrderBy(pair => pair.Key.Facet).ThenBy(pair => pair.Key.Key).Select(pair => $"{pair.Key.Facet}:{pair.Key.Key}:{pair.Value}"));

        public decimal Get(RecipeDiversityFacet facet, string key) =>
            _usage.GetValueOrDefault((facet, key));

        public FacetUsage Clone()
        {
            var clone = new FacetUsage();
            foreach (var (key, value) in _usage)
                clone._usage[key] = value;
            return clone;
        }

        public void Add(CandidateRecipe candidate, decimal amount)
        {
            foreach (var (facet, _) in VarietyFacets)
                foreach (var value in Values(candidate, facet))
                    Add(facet, value.Key, amount);
        }

        private void Add(Guid recipeId, RecipeDiversityProfile? profile, decimal amount)
        {
            Add(RecipeDiversityFacet.ExactRecipe, $"recipe:{recipeId:N}", amount);
            if (profile is null) return;
            foreach (var (facet, _) in VarietyFacets.Where(item => item.Facet != RecipeDiversityFacet.ExactRecipe))
                foreach (var value in profile.Values(facet))
                    Add(facet, value.Key, amount);
        }

        private void Add(RecipeDiversityFacet facet, string key, decimal amount) =>
            _usage[(facet, key)] = Get(facet, key) + amount;

        private static bool TryMapFacet(string? category, out RecipeDiversityFacet facet)
        {
            facet = category switch
            {
                nameof(RecipeSemanticTagCategory.Diet) => RecipeDiversityFacet.Diet,
                nameof(RecipeSemanticTagCategory.Protein) => RecipeDiversityFacet.Protein,
                nameof(RecipeSemanticTagCategory.Cuisine) => RecipeDiversityFacet.Cuisine,
                nameof(RecipeSemanticTagCategory.Flavor) => RecipeDiversityFacet.Flavor,
                _ => default,
            };
            return category is nameof(RecipeSemanticTagCategory.Diet)
                or nameof(RecipeSemanticTagCategory.Protein)
                or nameof(RecipeSemanticTagCategory.Cuisine)
                or nameof(RecipeSemanticTagCategory.Flavor);
        }
    }
}
