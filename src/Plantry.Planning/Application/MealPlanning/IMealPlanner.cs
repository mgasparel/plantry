using Plantry.Planning.Domain;

namespace Plantry.Planning.Application;

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
        IReadOnlyList<PlannedMealSummary> alreadyPlanned,
        RecentMealHistorySnapshot recentHistory,
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

/// <summary>
/// Week-level context (plantry-6mux) describing one already-planned meal so the AI planner can
/// treat existing meals as soft variety guidance rather than being blind to them. Names, not IDs —
/// IDs are meaningless to the model, and a planned recipe may fall outside the 50-cap candidate
/// list passed via <see cref="PlannerMealSlotContext.CandidateRecipes"/>. Covers the WHOLE week's
/// planned meals, including cells outside the current generation scope (a per-cell Regenerate needs
/// to know what else is planned) — see <see cref="GeneratePlanService"/> step 4.
/// </summary>
/// <param name="Date">The date of the already-planned meal.</param>
/// <param name="SlotLabel">The meal slot's display label (e.g. "Dinner"); falls back to the raw
/// slot id string when the slot has since been deleted from the household's slot configuration.</param>
/// <param name="DishNames">
/// Display names of the dishes in this meal. A dish whose name could not be resolved (a deleted
/// recipe or archived/removed product) is skipped entirely rather than surfacing a raw GUID —
/// never a placeholder like "Unknown recipe".
/// </param>
public sealed record PlannedMealSummary(
    DateOnly Date,
    string SlotLabel,
    IReadOnlyList<string> DishNames);

