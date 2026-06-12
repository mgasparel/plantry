using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Summary of a committed <see cref="ImportSession"/> for the post-commit Done screen. Derived entirely
/// from the committed lines — no new domain behaviour.
/// </summary>
/// <param name="ItemsAdded">Count of committed lines.</param>
/// <param name="StockedValue">Sum of <see cref="ImportLine.Price"/> across committed lines.</param>
/// <param name="CategoryCount">Count of distinct category IDs from new-product committed lines
/// (<see cref="ImportLine.NewProductCategoryId"/>). Existing-product lines do not carry category info
/// on the line entity, so only new-product categories are reflected here.</param>
/// <param name="SoonestExpiry">Earliest <see cref="ImportLine.ExpiryDate"/> among committed lines, or null
/// if no committed line has an expiry.</param>
/// <param name="MerchantText">The merchant / store name from the session, for display copy.</param>
public sealed record CommittedSessionSummary(
    int ItemsAdded,
    decimal StockedValue,
    int CategoryCount,
    DateOnly? SoonestExpiry,
    string? MerchantText);

/// <summary>
/// Read query for the Done screen: loads a <see cref="ImportSession"/> by id, validates it is
/// <see cref="ImportStatus.Committed"/> and belongs to the requesting household, and projects the
/// committed lines into a <see cref="CommittedSessionSummary"/>. Tenant-scoped via
/// <see cref="ITenantContext"/>; the repository's <c>FindAsync</c> applies the household filter
/// (mirrors the RLS layer).
/// </summary>
public sealed class GetCommittedSessionSummaryQuery(
    ImportSessionId sessionId,
    IImportSessionRepository sessions,
    ITenantContext tenant)
{
    public async Task<Result<CommittedSessionSummary>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;

        // Guard: only show the Done screen for a committed session.
        if (session.Status != ImportStatus.Committed)
            return Error.Custom("Intake.SessionNotCommitted", $"Session is not committed (status: {session.Status}).");

        var committedLines = session.Lines
            .Where(l => l.Status == LineStatus.Committed)
            .ToList();

        var itemsAdded = committedLines.Count;
        var stockedValue = committedLines.Sum(l => l.Price ?? 0m);
        var categoryCount = committedLines
            .Where(l => l.IsNewProduct && l.NewProductCategoryId.HasValue)
            .Select(l => l.NewProductCategoryId!.Value)
            .Distinct()
            .Count();
        var soonestExpiry = committedLines
            .Where(l => l.ExpiryDate.HasValue)
            .Select(l => (DateOnly?)l.ExpiryDate!.Value)
            .OrderBy(d => d)
            .FirstOrDefault();

        return new CommittedSessionSummary(
            itemsAdded,
            stockedValue,
            categoryCount,
            soonestExpiry,
            session.MerchantText);
    }
}
