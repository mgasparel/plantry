using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

public sealed record RecipeCreatedEvent(
    RecipeId RecipeId,
    HouseholdId HouseholdId,
    DateTimeOffset CreatedAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt => CreatedAt;
}
