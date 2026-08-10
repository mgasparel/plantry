namespace Plantry.Planning.Domain;

/// <summary>
/// Deterministic pre-ordering for the bounded candidate snapshot. Hard constraints and ACL validation
/// remain separate; this is a soft evidence signal that makes the configured Cost/Waste weights
/// observable in the candidate list order.
/// </summary>
public static class CandidateRecipeOrdering
{
    public static IReadOnlyList<CandidateRecipe> Order(
        IReadOnlyList<CandidateRecipe> candidates,
        PlanningWeights weights)
    {
        if (candidates.Count <= 1) return candidates;

        var completeCosts = candidates
            .Where(c => c.CostCompleteness == CandidateCostCompleteness.Complete && c.CostPerServing.HasValue)
            .Select(c => c.CostPerServing!.Value)
            .ToList();
        var minCost = completeCosts.Count == 0 ? 0m : completeCosts.Min();
        var maxCost = completeCosts.Count == 0 ? 0m : completeCosts.Max();

        return candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = weights.Waste * WasteScore(candidate) + weights.Cost * CostScore(
                    candidate, completeCosts.Count > 0, minCost, maxCost) +
                    weights.Variety * VarietyScore(candidate),
            })
            .OrderByDescending(x => x.Score)
            // Recipe name is presentation data, never a ranking input. Identity is the stable final tie-break.
            .ThenBy(x => x.Candidate.RecipeId)
            .Select(x => x.Candidate)
            .ToList();
    }

    private static decimal WasteScore(CandidateRecipe candidate) =>
        candidate.HasContributingExpiringStock == true ? 1m : 0m;

    private static decimal VarietyScore(CandidateRecipe candidate)
    {
        var profile = candidate.DiversityProfile;
        if (profile is null) return 0m;
        var confirmed = new[] { RecipeDiversityFacet.Protein, RecipeDiversityFacet.Cuisine,
            RecipeDiversityFacet.Flavor, RecipeDiversityFacet.Diet }
            .Count(f => profile.Confidence(f) == RecipeDiversityConfidence.Confirmed);
        return confirmed / 4m;
    }


    private static decimal CostScore(
        CandidateRecipe candidate,
        bool hasCompleteCosts,
        decimal minCost,
        decimal maxCost)
    {
        // Unknown and partial evidence are never treated as a zero-dollar recipe. When at least one
        // complete price exists, placing them below the complete set is deterministic and conservative;
        // when all costs are unresolved, cost contributes no fabricated preference.
        if (candidate.CostCompleteness != CandidateCostCompleteness.Complete || !candidate.CostPerServing.HasValue)
            return hasCompleteCosts ? -1m : 0m;

        if (maxCost == minCost) return 0.5m;
        return (maxCost - candidate.CostPerServing.Value) / (maxCost - minCost);
    }
}
