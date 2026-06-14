using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

public sealed record RecipeUpdatedEvent(
    RecipeId RecipeId,
    HouseholdId HouseholdId,
    DateTimeOffset UpdatedAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt => UpdatedAt;
}
