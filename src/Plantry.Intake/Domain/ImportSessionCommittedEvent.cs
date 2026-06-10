using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Intake.Domain;

public sealed record ImportSessionCommittedEvent(
    ImportSessionId SessionId,
    HouseholdId HouseholdId,
    DateTimeOffset CommittedAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt => CommittedAt;
}
