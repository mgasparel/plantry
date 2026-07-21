using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Housekeeping.Application;

/// <summary>
/// Builds the Tidy Up page read model (tidy-up.md §4): runs every registered <see cref="IProblemDetector"/>,
/// loads the household's tombstones in one batch, splits each detector's findings into open (no
/// matching tombstone) and dismissed (a tombstone whose <see cref="Dismissal.FactsFingerprint"/> matches
/// the finding as currently computed), groups the open findings by detector (severity-first, then
/// detector id), and refreshes the badge-count cache (T6) with the fresh open count — the <b>only</b>
/// place that ever writes a real count into <see cref="ITidyUpBadgeCache"/>.
/// </summary>
public sealed class GetTidyUpPageQuery(
    IEnumerable<IProblemDetector> detectors,
    IDismissalRepository dismissals,
    ITidyUpBadgeCache badgeCache,
    ITenantContext tenant)
{
    public async Task<TidyUpPageResult> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return TidyUpPageResult.Empty;

        var householdId = HouseholdId.From(householdGuid);
        var tombstones = await dismissals.ListForHouseholdAsync(householdId, ct);
        var tombstonesByKey = tombstones
            .ToLookup(t => (t.DetectorId, t.SubjectId));

        var groups = new List<TidyUpGroup>();
        var dismissedRows = new List<DismissedFindingRow>();
        var openCount = 0;

        foreach (var detector in detectors.OrderBy(d => d.Severity).ThenBy(d => d.Id.Value, StringComparer.Ordinal))
        {
            var findings = await detector.DetectAsync(ct);
            var openRows = new List<OpenFindingRow>();

            foreach (var finding in findings)
            {
                var tombstone = tombstonesByKey[(finding.DetectorId, finding.SubjectId)].FirstOrDefault();
                if (tombstone is not null && tombstone.FactsFingerprint == finding.FactsFingerprint)
                {
                    dismissedRows.Add(new DismissedFindingRow(finding, tombstone.DismissedAtUtc));
                }
                else
                {
                    openRows.Add(new OpenFindingRow(finding));
                }
            }

            openCount += openRows.Count;

            // Empty groups don't render (T2) — a detector with nothing open this pass produces no card,
            // even if some of its findings are sitting dismissed (those still appear in the flat
            // disclosure below).
            if (openRows.Count > 0)
            {
                groups.Add(new TidyUpGroup(
                    detector.Id, detector.GroupTitle, detector.GroupConsequence, detector.IconName,
                    detector.Severity, openRows));
            }
        }

        await badgeCache.SetAsync(householdId, openCount, ct);

        var orderedDismissed = dismissedRows
            .OrderByDescending(d => d.DismissedAtUtc)
            .ToList();

        return new TidyUpPageResult(groups, orderedDismissed, openCount);
    }
}
