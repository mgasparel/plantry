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

        // Reserve only the minimum hard-feasibility witness before soft coverage. A restrictive
        // required stance needs one eligible candidate to remain feasible; reserving the entire
        // eligible corpus would crowd out confirmed diversity facets.
        var hasHardConstraint = constraints.RestrictedTagIds.Count > 0 ||
            constraints.AttendeeStances.Any(a => a.RequiredTagIds.Count > 0);
        if (hasHardConstraint)
        {
            var hardRepresentative = CandidateRecipeOrdering.Order(
                candidates.Where(IsHardEligible).ToList(), weights).FirstOrDefault();
            if (hardRepresentative is not null) Add(hardRepresentative);
        }

        // Reserve confirmed facet representatives next. This guarantees coverage even when a large
        // required-eligible set would otherwise consume the cap.
        foreach (var facet in new[] { RecipeDiversityFacet.Protein, RecipeDiversityFacet.Cuisine,
                                      RecipeDiversityFacet.Flavor, RecipeDiversityFacet.Diet })
        {
            var values = candidates.SelectMany(c => c.DiversityProfile?.Values(facet) ?? [])
                .Where(v => v.Source == RecipeDiversityEvidenceSource.ConfirmedTag)
                .GroupBy(v => v.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var value in values)
            {
                var representative = CandidateRecipeOrdering.Order(
                    candidates.Where(c => c.DiversityProfile?.Values(facet).Any(v => v.Key == value.Key) == true).ToList(), weights)
                    .FirstOrDefault();
                if (representative is not null) Add(representative);
            }
        }

        foreach (var candidate in CandidateRecipeOrdering.Order(candidates, weights))
            Add(candidate);

        return selected;
    }
}
