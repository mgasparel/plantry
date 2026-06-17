namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Transient DTO representing an AI-proposed meal for a single cell.
/// Not an EF entity — lives in-memory (pending store) only until accepted or discarded.
/// </summary>
public sealed record ProposedMeal(
    DateOnly Date,
    MealSlotId MealSlotId,
    IReadOnlyList<Guid> EffectiveAttendees,
    IReadOnlyList<ProposedDish> Dishes,
    string? Reasoning);

/// <summary>One proposed dish within a <see cref="ProposedMeal"/>.</summary>
public sealed record ProposedDish(Guid RecipeId, int Servings, int Ordinal);
