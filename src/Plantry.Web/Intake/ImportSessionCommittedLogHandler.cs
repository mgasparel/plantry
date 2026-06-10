using Plantry.Intake.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Intake;

/// <summary>
/// Minimal handler for <see cref="ImportSessionCommittedEvent"/> — logs that a receipt intake committed.
/// Proves the dispatcher seam end-to-end (Slice 6c) and is the hook later slices replace/augment with
/// real reactions (e.g. notifications, projections) without touching the commit path.
/// </summary>
public sealed class ImportSessionCommittedLogHandler(ILogger<ImportSessionCommittedLogHandler> logger)
    : IDomainEventHandler<ImportSessionCommittedEvent>
{
    public Task HandleAsync(ImportSessionCommittedEvent domainEvent, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Import session {SessionId} committed for household {HouseholdId} at {CommittedAt:o}.",
            domainEvent.SessionId.Value, domainEvent.HouseholdId.Value, domainEvent.CommittedAt);
        return Task.CompletedTask;
    }
}
