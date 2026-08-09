using Plantry.Intake.Domain;
using Plantry.SharedKernel;

namespace Plantry.Intake.Application;

/// <summary>
/// The household's trailing-average committed-basket total — the Intake review trip-context stat
/// (stats-page-prototype.html injection appendix: "Trip footer: this basket vs your average",
/// plantry-bb7p). Surfaced on the Review page so an in-progress (not-yet-committed) basket total can be
/// compared against recent history.
/// </summary>
public sealed class GetTrailingAverageBasketQuery(IImportSessionRepository sessions)
{
    /// <summary>Number of most-recent committed baskets the trailing average is computed over — matches
    /// the <c>take</c> default other "recent" reads in this repository use (<see cref="IImportSessionRepository.ListRecentAsync"/>).</summary>
    public const int WindowSize = 10;

    /// <summary>Null when the household has no committed baskets yet — nothing to average against, so the
    /// caller shows no trip-context chip rather than a misleading average of zero.</summary>
    public async Task<decimal?> ExecuteAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        var totals = await sessions.ListRecentCommittedTotalsAsync(householdId, WindowSize, ct);
        return totals.Count > 0 ? Math.Round(totals.Average(), 2) : null;
    }
}
