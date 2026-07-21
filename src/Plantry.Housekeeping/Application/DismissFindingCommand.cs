using Microsoft.Extensions.Logging;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Housekeeping.Application;

/// <summary>
/// Dismisses one finding (T5): writes (or, if a stale tombstone already sits at this key,
/// <see cref="Dismissal.Supersede"/>s) the tombstone with the fingerprint <b>from the finding as
/// rendered</b> — so a fact change between render and click harmlessly re-surfaces the finding rather
/// than suppressing a gap the user never actually saw dismissed (§4). Invalidates the badge cache so
/// the nav count reflects the dismissal on the very next read.
/// </summary>
public sealed class DismissFindingCommand(
    IDismissalRepository dismissals, ITidyUpBadgeCache badgeCache, IClock clock,
    ILogger<DismissFindingCommand> logger)
{
    public async Task ExecuteAsync(
        HouseholdId householdId, DetectorId detectorId, Guid subjectId, string factsFingerprint,
        CancellationToken ct = default)
    {
        var existing = await dismissals.FindAsync(householdId, detectorId, subjectId, ct);
        if (existing is not null)
        {
            existing.Supersede(factsFingerprint, clock);
        }
        else
        {
            await dismissals.AddAsync(
                Dismissal.Create(householdId, detectorId, subjectId, factsFingerprint, clock), ct);
        }

        await dismissals.SaveChangesAsync(ct);
        await badgeCache.InvalidateAsync(householdId, ct);

        logger.LogInformation(
            "Dismissed finding {DetectorId}/{SubjectId} for household {HouseholdId} (superseded existing: {Superseded}).",
            detectorId.Value, subjectId, householdId.Value, existing is not null);
    }
}
