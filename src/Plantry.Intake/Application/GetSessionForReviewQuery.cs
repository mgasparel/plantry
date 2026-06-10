using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

public sealed record ReviewLineView(
    Guid LineId,
    int LineNo,
    string ReceiptText,
    SuggestedConfidence SuggestedConfidence,
    LineStatus Status,
    Guid? ProductId,
    Guid? SkuId,
    decimal? Quantity,
    Guid? UnitId,
    Guid? LocationId,
    DateOnly? ExpiryDate,
    decimal? Price,
    bool IsNewProduct,
    string? NewProductName,
    Guid? NewProductCategoryId);

/// <summary>
/// The session header plus its lines and the Catalog reference data (dropdown options) needed to render
/// the review form in one shot — so the page makes a single application call.
/// </summary>
public sealed record SessionReviewView(
    Guid SessionId,
    ImportStatus Status,
    string? MerchantText,
    string? ParseError,
    IReadOnlyList<ReviewLineView> Lines,
    ReviewReferenceData ReferenceData);

/// <summary>
/// Read query (SPEC §2e): loads a <see cref="ImportSession"/> with its lines and the household's Catalog
/// reference data for the review form. Tenant-scoped via <see cref="ITenantContext"/>; reference data is
/// sourced through <see cref="IReviewReferenceDataProvider"/> rather than a direct Catalog dependency
/// (the Intake context stays decoupled from Catalog, per the bounded-context boundary).
/// </summary>
public sealed class GetSessionForReviewQuery(
    ImportSessionId sessionId,
    IImportSessionRepository sessions,
    IReviewReferenceDataProvider referenceData,
    ITenantContext tenant)
{
    public async Task<Result<SessionReviewView>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;

        var reference = await referenceData.GetAsync(ct);

        var lines = session.Lines
            .OrderBy(l => l.LineNo)
            .Select(l => new ReviewLineView(
                l.Id.Value,
                l.LineNo,
                l.ReceiptText,
                l.SuggestedConfidence,
                l.Status,
                l.ProductId,
                l.SkuId,
                l.Quantity,
                l.UnitId,
                l.LocationId,
                l.ExpiryDate,
                l.Price,
                l.IsNewProduct,
                l.NewProductName,
                l.NewProductCategoryId))
            .ToList();

        return new SessionReviewView(
            session.Id.Value,
            session.Status,
            session.MerchantText,
            session.ParseError,
            lines,
            reference);
    }
}
