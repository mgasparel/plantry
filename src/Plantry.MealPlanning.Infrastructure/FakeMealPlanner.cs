using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;

namespace Plantry.MealPlanning.Infrastructure;

/// <summary>
/// Deterministic, no-network fake <see cref="IMealPlanner"/> for E2E / integration tests.
/// Returns exactly one proposal per slot, always using the first candidate recipe that does not
/// violate any hard Restricted stance in the slot's constraints. If no candidate passes, returns
/// no proposal for that slot (unfilled). Never makes a network call.
/// </summary>
public sealed class FakeMealPlanner : IMealPlanner
{
    public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slotsContext,
        PlanningWeights weights,
        CancellationToken ct = default)
    {
        var proposals = new List<ProposedMeal>();

        foreach (var ctx in slotsContext)
        {
            // Pick the first candidate that has no restricted tags for this slot
            var candidate = ctx.CandidateRecipes.FirstOrDefault(r =>
                !r.TagIds.Any(t => ctx.Constraints.RestrictedTagIds.Contains(t)));

            if (candidate is null) continue;

            proposals.Add(new ProposedMeal(
                Date: ctx.Date,
                MealSlotId: ctx.MealSlotId,
                EffectiveAttendees: ctx.EffectiveAttendees,
                Dishes: [new ProposedDish(candidate.RecipeId, candidate.DefaultServings, Ordinal: 1)],
                Reasoning: $"Fake: selected {candidate.Name}"));
        }

        return Task.FromResult<IReadOnlyList<ProposedMeal>>(proposals);
    }
}
