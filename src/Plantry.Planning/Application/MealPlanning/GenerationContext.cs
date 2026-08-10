using Plantry.Planning.Domain;

namespace Plantry.Planning.Application;

/// <summary>Server-owned context for one empty meal-slot candidate set.</summary>
public sealed record PlannerMealSlotContext(
    DateOnly Date,
    MealSlotId MealSlotId,
    string SlotLabel,
    IReadOnlyList<Guid> EffectiveAttendees,
    GenerationConstraints Constraints,
    IReadOnlyList<CandidateRecipe> CandidateRecipes);

/// <summary>One already-planned meal used as soft week-level diversity context.</summary>
public sealed record PlannedMealSummary(
    DateOnly Date,
    string SlotLabel,
    IReadOnlyList<string> DishNames,
    IReadOnlyList<PlannedRecipeSummary>? RecipeChoices = null,
    bool IsPending = false);

/// <summary>Identity and confirmed diversity facts for a recipe already in the week.</summary>
public sealed record PlannedRecipeSummary(
    Guid RecipeId,
    RecipeDiversityProfile? DiversityProfile = null);
