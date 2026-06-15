using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Emitted by <c>CookRecipe</c> after the <see cref="CookEvent"/> is written and all consumes
/// complete successfully within the transaction (recipes-domain-model.md §9, O2).
/// <para>
/// <see cref="CookedBy"/> mirrors <see cref="CookEvent.CookedBy"/> — the identity of the user who
/// initiated the cook (O2). <see cref="ServingsCooked"/> is the materialized scaled servings count
/// (§4, J4).
/// </para>
/// </summary>
public sealed record RecipeCookedEvent(
    RecipeId RecipeId,
    HouseholdId HouseholdId,
    int ServingsCooked,
    Guid CookedBy,
    DateTimeOffset CookedAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt => CookedAt;
}
