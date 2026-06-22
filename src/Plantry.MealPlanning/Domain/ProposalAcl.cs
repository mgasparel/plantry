namespace Plantry.MealPlanning.Domain;

/// <summary>
/// ACL validation layer for AI-proposed meals (ADR-007: AI is an untrusted external function).
/// Validates each raw AI proposal against the current candidate recipe list and the resolved
/// generation constraints. Enforces:
///   - Recipe must exist in the candidate list (no hallucinated IDs)
///   - If a dish's recipe carries any attendee's Restricted tag: drop that dish; if no valid dishes
///     remain, return unfilled.
///   - Every attendee's Required tags (M5) must be satisfied by ≥1 SURVIVING dish covering ALL of
///     that attendee's RequiredTagIds. If any attendee is uncovered, return unfilled.
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
        var validDishes = new List<(ProposedDish Dish, CandidateRecipe Recipe)>();

        // Derived union of all Restricted tag IDs (fast drop test).
        var allRestrictedTagIds = constraints.RestrictedTagIds;

        foreach (var dish in proposed.Dishes)
        {
            // Reject hallucinated recipe IDs.
            if (!candidateMap.TryGetValue(dish.RecipeId, out var recipe))
                continue;

            // Drop any dish that carries any attendee's Restricted tag.
            var hasRestricted = recipe.TagIds.Any(t => allRestrictedTagIds.Contains(t));
            if (hasRestricted)
                continue;

            validDishes.Add((dish, recipe));
        }

        if (validDishes.Count == 0)
            return AclValidationResult.Unfilled;

        // M5 (per-attendee Required satisfaction): every attendee in AttendeeStances must have
        // ≥1 surviving dish whose recipe.TagIds covers ALL of that attendee's RequiredTagIds.
        // An attendee with no Required tags is always satisfied.
        foreach (var attendee in constraints.AttendeeStances)
        {
            if (attendee.RequiredTagIds.Count == 0)
                continue;

            var covered = validDishes.Any(vd =>
                attendee.RequiredTagIds.All(vd.Recipe.TagIds.Contains));

            if (!covered)
                return AclValidationResult.Unfilled;
        }

        var validated = new ProposedMeal(
            proposed.Date,
            proposed.MealSlotId,
            proposed.EffectiveAttendees,
            validDishes.Select(vd => vd.Dish).ToList(),
            proposed.Reasoning);

        return new AclValidationResult(IsValid: true, ValidatedProposal: validated);
    }
}

/// <summary>Result of ACL validation for a single AI-proposed meal.</summary>
public sealed record AclValidationResult(
    bool IsValid,
    ProposedMeal? ValidatedProposal)
{
    /// <summary>Singleton result for cells that have no valid recipe after ACL filtering.</summary>
    public static AclValidationResult Unfilled { get; } = new(IsValid: false, ValidatedProposal: null);
}
