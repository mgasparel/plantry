namespace Plantry.MealPlanning.Domain;

/// <summary>
/// ACL validation layer for AI-proposed meals (ADR-007: AI is an untrusted external function).
/// Validates each raw AI proposal against the current candidate recipe list and the resolved
/// generation constraints. Enforces:
///   - Recipe must exist in the candidate list (no hallucinated IDs)
///   - If a dish's recipe carries a Restricted tag: try to drop that dish; if no valid dishes remain,
///     return unfilled (simplified C6 for P3-6a — no per-attendee sub-split yet)
/// Raw AI output is NEVER persisted — only the validated ProposedMeal reaches the store.
/// </summary>
public static class ProposalAcl
{
    /// <summary>
    /// Validates a single <see cref="ProposedMeal"/> against the current candidate recipes and constraints.
    /// </summary>
    public static AclValidationResult Validate(
        ProposedMeal proposed,
        IReadOnlyList<CandidateRecipe> candidates,
        GenerationConstraints constraints)
    {
        var candidateMap = candidates.ToDictionary(c => c.RecipeId);
        var validDishes = new List<ProposedDish>();
        var wasSplit = false;

        foreach (var dish in proposed.Dishes)
        {
            // Reject hallucinated recipe IDs
            if (!candidateMap.TryGetValue(dish.RecipeId, out var recipe))
                continue;

            // Check whether this recipe violates any Restricted stance
            var hasRestricted = recipe.TagIds.Any(t => constraints.RestrictedTagIds.Contains(t));
            if (hasRestricted)
            {
                wasSplit = true;
                continue; // Drop this dish — simplified C6 for P3-6a
            }

            validDishes.Add(dish);
        }

        if (validDishes.Count == 0)
        {
            return AclValidationResult.Unfilled;
        }

        var validated = new ProposedMeal(
            proposed.Date,
            proposed.MealSlotId,
            proposed.EffectiveAttendees,
            validDishes,
            proposed.Reasoning);

        return new AclValidationResult(IsValid: true, ValidatedProposal: validated, WasSplit: wasSplit);
    }
}

/// <summary>Result of ACL validation for a single AI-proposed meal.</summary>
public sealed record AclValidationResult(
    bool IsValid,
    ProposedMeal? ValidatedProposal,
    bool WasSplit)
{
    /// <summary>Singleton result for cells that have no valid recipe after ACL filtering.</summary>
    public static AclValidationResult Unfilled { get; } = new(IsValid: false, ValidatedProposal: null, WasSplit: false);
}
