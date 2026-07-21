using Microsoft.Extensions.Logging;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;

namespace Plantry.Housekeeping.Application;

/// <summary>Restores a dismissed finding: deletes its tombstone outright (T5) and invalidates the badge cache.</summary>
public sealed class RestoreFindingCommand(
    IDismissalRepository dismissals, ITidyUpBadgeCache badgeCache, ILogger<RestoreFindingCommand> logger)
{
    public async Task ExecuteAsync(
        HouseholdId householdId, DetectorId detectorId, Guid subjectId, CancellationToken ct = default)
    {
        var existing = await dismissals.FindAsync(householdId, detectorId, subjectId, ct);
        if (existing is not null)
        {
            await dismissals.RemoveAsync(existing, ct);
            await dismissals.SaveChangesAsync(ct);
        }

        await badgeCache.InvalidateAsync(householdId, ct);

        logger.LogInformation(
            "Restored finding {DetectorId}/{SubjectId} for household {HouseholdId} (tombstone existed: {Existed}).",
            detectorId.Value, subjectId, householdId.Value, existing is not null);
    }
}
