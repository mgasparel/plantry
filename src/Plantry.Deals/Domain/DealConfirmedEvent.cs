using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// Raised when a <see cref="Deal"/> transitions to <see cref="DealStatus.Confirmed"/> — via a user
/// confirm, a memory <c>AutoConfirm</c>, or a <c>Correct</c> re-resolution (DJ4). Carries the resolved
/// <see cref="ProductId"/>, the <see cref="StoreId"/>, and whether the resolution was a memory
/// auto-match (<see cref="AutoMatched"/>). No subscriber today — emitted for downstream projections /
/// audit; dispatched post-save with no transactional outbox (ADR-014), so treat any future handler as
/// at-most-once.
/// </summary>
public sealed record DealConfirmedEvent(
    DealId DealId,
    HouseholdId HouseholdId,
    Guid ProductId,
    Guid StoreId,
    bool AutoMatched,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
}
