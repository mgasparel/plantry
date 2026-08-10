namespace Plantry.Planning.Domain;

/// <summary>Builds the bounded, deterministic optimizer working set from the full household corpus.</summary>
public static class CandidateRecipeShortlisting
{
    public const int MaximumWorkingSet = 200;

    public static IReadOnlyList<CandidateRecipe> Select(
        IReadOnlyList<CandidateRecipe> candidates,
        GenerationConstraints constraints,
        PlanningWeights weights)
    {
        if (candidates.Count <= MaximumWorkingSet)
            return candidates;

        bool IsHardEligible(CandidateRecipe candidate) =>
            !candidate.TagIds.Any(constraints.RestrictedTagIds.Contains) &&
            constraints.AttendeeStances.All(attendee =>
                attendee.RequiredTagIds.All(candidate.TagIds.Contains));

        var selected = new List<CandidateRecipe>(MaximumWorkingSet);
        var selectedIds = new HashSet<Guid>();
        void Add(CandidateRecipe candidate)
        {
            if (selected.Count < MaximumWorkingSet && selectedIds.Add(candidate.RecipeId))
                selected.Add(candidate);
        }

        // Hard constraints always win over the soft working-set limit.
        foreach (var candidate in candidates.Where(IsHardEligible).OrderBy(c => c.RecipeId))
            Add(candidate);

        // Preserve one representative for each confirmed semantic facet before soft ranking.
        foreach (var facet in new[] { RecipeDiversityFacet.Protein, RecipeDiversityFacet.Cuisine,
                                      RecipeDiversityFacet.Flavor, RecipeDiversityFacet.Diet })
        {
            foreach (var candidate in candidates
                         .Where(c => c.DiversityProfile?.Confidence(facet) == RecipeDiversityConfidence.Confirmed)
                         .OrderBy(c => c.RecipeId))
            {
                var values = candidate.DiversityProfile!.Values(facet);
                if (values.Any(v => selected.All(s => s.DiversityProfile is null ||
                        !s.DiversityProfile.Values(facet).Any(existing => existing.Key == v.Key))))
                    Add(candidate);
            }
        }

        foreach (var candidate in CandidateRecipeOrdering.Order(candidates, weights))
            Add(candidate);

        return selected;
    }
}
