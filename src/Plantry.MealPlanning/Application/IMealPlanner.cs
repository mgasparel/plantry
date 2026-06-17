using Plantry.MealPlanning.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Port for the AI meal-planning service (ADR-007: untrusted external function).
/// Raw output from this port is ALWAYS validated through <see cref="ProposalAcl"/> before use —
/// never persisted directly.
/// </summary>
public interface IMealPlanner
{
    /// <summary>
    /// Proposes meals for the given empty slots. Returns a list of raw proposals (unvalidated).
    /// Soft failures return an empty list — never throws.
    /// </summary>
    Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slotsContext,
        PlanningWeights weights,
        CancellationToken ct = default);
}

/// <summary>
/// Context passed to the AI planner for one empty meal slot cell.
/// Contains everything the AI needs to propose a recipe: date, attendee constraints, candidates.
/// </summary>
public sealed record PlannerMealSlotContext(
    DateOnly Date,
    MealSlotId MealSlotId,
    string SlotLabel,
    IReadOnlyList<Guid> EffectiveAttendees,
    GenerationConstraints Constraints,
    IReadOnlyList<CandidateRecipe> CandidateRecipes);

