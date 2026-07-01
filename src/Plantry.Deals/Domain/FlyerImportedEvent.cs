using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// Raised by <see cref="FlyerImport.MarkParsed"/> (first parse) and <see cref="FlyerImport.RecordRepull"/>
/// (a changed re-pull) once ingestion has materialized this pull's <see cref="Deal"/>s (DJ2 step 7 / §9).
/// Carries the <see cref="PendingCount"/> — the number of deals left <see cref="DealStatus.Pending"/> for
/// review at parse time — which feeds the Home "N deals to review" banner (§0b / plantry-bpw) and the
/// Deals-page review section.
/// <para>
/// <b><see cref="PendingCount"/> is point-in-time</b> (§9): correct at parse, not a live count — a later
/// review resolves deals without re-emitting. No subscriber today (latent, like <c>DealConfirmed</c>);
/// dispatched post-save with no transactional outbox (ADR-014), so treat any future handler as
/// at-most-once.
/// </para>
/// </summary>
public sealed record FlyerImportedEvent(
    FlyerImportId FlyerImportId,
    HouseholdId HouseholdId,
    Guid StoreId,
    int PendingCount,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
}
