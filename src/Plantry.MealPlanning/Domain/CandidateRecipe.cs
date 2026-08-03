namespace Plantry.MealPlanning.Domain;

/// <summary>
/// A recipe candidate supplied to the AI planner, containing the minimum facts needed for recipe selection.
/// Lives in Domain so it can be used by <see cref="ProposalAcl"/> without a circular dependency.
/// Cross-context read via <c>IRecipeReadModel</c> — MealPlanning never accesses Recipes tables directly.
/// </summary>
/// <param name="HouseholdAvgRating">
/// Household-wide average rating (1dp), across every member who has rated — the fallback signal when
/// this slot's attendees haven't rated (plantry-zlwp.5). Null when nobody in the household has rated.
/// </param>
/// <param name="RatedCount">Count of household members who have rated this recipe at all.</param>
/// <param name="AttendeeStars">
/// This slot's <c>DefaultAttendees</c> who HAVE rated this recipe, keyed by user id → their 1-5 stars.
/// Scoped per slot (built fresh for each <see cref="PlannerMealSlotContext"/>) because attendees vary
/// by slot — an attendee absent from this dictionary simply hasn't rated (use
/// <see cref="HouseholdAvgRating"/> as the fallback signal for them). Soft signal only: the planner
/// should favour highly-rated recipes for present attendees, never hard-filter on a low rating —
/// that stays reserved for Required/Restricted tag stances (<see cref="ProposalAcl"/>).
/// Null (not empty) when no attendee has rated.
/// </param>
public sealed record CandidateRecipe(
    Guid RecipeId,
    string Name,
    IReadOnlyList<Guid> TagIds,
    int DefaultServings,
    decimal? CostPerServing,
    decimal? HouseholdAvgRating = null,
    int RatedCount = 0,
    IReadOnlyDictionary<Guid, int>? AttendeeStars = null);
