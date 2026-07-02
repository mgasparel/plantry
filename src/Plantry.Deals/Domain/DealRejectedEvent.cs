using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// Raised when a <see cref="Deal"/> transitions to <see cref="DealStatus.Rejected"/> (DJ4). Emitted only
/// on the actual transition, not on an idempotent re-reject. No subscriber today — emitted for downstream
/// projections / audit; dispatched post-save with no transactional outbox (ADR-014), so treat any future
/// handler as at-most-once.
/// </summary>
public sealed record DealRejectedEvent(
    DealId DealId,
    HouseholdId HouseholdId,
    Guid StoreId,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
}
