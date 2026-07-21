using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>One receipt line on the committed-session detail view (receipt-intake-history.md H8).</summary>
/// <param name="ProductId">The existing product this line resolved to; null for a dismissed line, or a
/// line that minted a brand-new product (see <paramref name="CreatedProductId"/> instead).</param>
/// <param name="CreatedProductId">Set when this line created its product at commit (ADR-010) — drives the
/// "New product" badge. Mutually exclusive with <paramref name="ProductId"/>.</param>
/// <param name="Quantity">Null for a line dismissed before it was ever confirmed (nothing to show but "—").</param>
/// <param name="UnitId">Null under the same condition as <paramref name="Quantity"/>.</param>
/// <param name="Price">The user-resolved price when set, else the AI-suggested price — so a dismissed line
/// that was never confirmed can still show the receipt's own price.</param>
/// <param name="HasEachEstimate">Drives the "≈ N each" badge (weight→each, plantry-1mu).</param>
/// <param name="IsDismissed">Drives the struck-through "Dismissed during review" row treatment (H8) — the
/// receipt is a factual record, so a dismissed line stays visible rather than being hidden.</param>
public sealed record CommittedLineRow(
    Guid ImportLineId,
    int LineNo,
    string ReceiptText,
    Guid? ProductId,
    Guid? CreatedProductId,
    decimal? Quantity,
    Guid? UnitId,
    decimal? Price,
    bool HasEachEstimate,
    bool IsDismissed);

/// <summary>The read-only detail view of one committed intake session (receipt-intake-history.md H7/H8).</summary>
public sealed record CommittedSessionDetail(
    ImportSessionId Id,
    string? MerchantText,
    DateOnly? PurchaseDate,
    TimeOnly? PurchaseTime,
    DateTimeOffset? CommittedAt,
    DateTimeOffset CreatedAt,
    decimal? Subtotal,
    decimal? Tax,
    decimal? Total,
    string? ReceiptNumber,
    string? PaymentDescriptor,
    Guid UserId,
    IReadOnlyList<CommittedLineRow> Lines);

/// <summary>
/// Read query for <c>/Intake/Session</c> (receipt-intake-history.md H7): loads a session by id, guards it
/// to <see cref="ImportStatus.Committed"/> only (a Ready session has no finished detail to show — the page
/// model redirects it to Review; other statuses redirect to History), and projects its lines in receipt
/// order (<see cref="ImportLine.LineNo"/>) for the line grid. Tenant-scoped via <see cref="ITenantContext"/>;
/// <see cref="IImportSessionRepository.FindAsync"/> applies the household filter (mirrors RLS).
/// </summary>
public sealed class GetCommittedSessionDetailQuery(
    ImportSessionId sessionId,
    IImportSessionRepository sessions,
    ITenantContext tenant)
{
    public async Task<Result<CommittedSessionDetail>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;

        if (session.Status != ImportStatus.Committed)
            return Error.Custom("Intake.SessionNotCommitted", $"Session is not committed (status: {session.Status}).");

        // By the time a session is Committed every line is either Committed or Dismissed (the strict
        // commit gate blocks the whole commit on any still-Pending line) — the status filter here is
        // defensive, not load-bearing. Ordered by LineNo so the grid preserves the receipt's own shape.
        var lines = session.Lines
            .Where(l => l.Status is LineStatus.Committed or LineStatus.Dismissed)
            .OrderBy(l => l.LineNo)
            .Select(l => new CommittedLineRow(
                l.Id.Value,
                l.LineNo,
                l.ReceiptText,
                l.ProductId,
                l.CreatedProductId,
                l.Quantity,
                l.UnitId,
                l.Price ?? l.SuggestedPrice,
                l.HasEachEstimate,
                l.Status == LineStatus.Dismissed))
            .ToList();

        return new CommittedSessionDetail(
            session.Id,
            session.MerchantText,
            session.PurchaseDate,
            session.PurchaseTime,
            session.CommittedAt,
            session.CreatedAt,
            session.Subtotal,
            session.Tax,
            session.Total,
            session.ReceiptNumber,
            session.PaymentDescriptor,
            session.UserId,
            lines);
    }
}
