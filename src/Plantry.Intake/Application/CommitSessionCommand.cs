using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Commits a <c>Ready</c> <see cref="ImportSession"/> to the pantry (ADR-010): for each confirmed line,
/// in its own transaction, create the product if it is new (Catalog), record the purchased lot
/// (Inventory, <c>source = Intake</c>), and record the purchase price (Pricing) — then mark the line
/// committed with those refs. Finally the session itself is marked committed (raising
/// <c>ImportSessionCommittedEvent</c>).
///
/// <para><b>Resumable.</b> Each line is saved with its committed refs before the next line runs, and
/// non-confirmed lines (including already-committed ones) are skipped — so a mid-batch failure can be
/// retried and re-runs cleanly without double-writing committed lines. (A failure <em>within</em> a single
/// line — e.g. after stock but before price — is not transactional across contexts; that line re-runs on
/// retry. Acceptable for Phase 1; revisit with an outbox if it bites.)</para>
/// </summary>
public sealed class CommitSessionCommand(
    ImportSessionId sessionId,
    IImportSessionRepository sessions,
    ICreateProductPort createProduct,
    IAddStockPort addStock,
    IRecordPricePort recordPrice,
    IClock clock,
    ITenantContext tenant,
    ILogger<CommitSessionCommand> logger)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;
        if (session.Status != ImportStatus.Ready)
            return Error.Custom("Intake.SessionNotReady", $"Cannot commit a session in status '{session.Status}'.");

        var purchasedOn = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        try
        {
            foreach (var line in session.Lines)
            {
                if (line.Status != LineStatus.Confirmed)
                    continue; // skip Pending / Dismissed / already-Committed (resumability)

                Guid productId;
                Guid? createdProductId = null;
                if (line.IsNewProduct)
                {
                    productId = await createProduct.CreateAsync(
                        line.NewProductName!, line.NewProductCategoryId!.Value, line.UnitId!.Value, ct);
                    createdProductId = productId;
                }
                else
                {
                    productId = line.ProductId!.Value;
                }

                var journalId = await addStock.AddStockAsync(
                    productId, line.SkuId, line.Quantity!.Value, line.UnitId!.Value, line.LocationId!.Value,
                    line.ExpiryDate, purchasedOn, session.UserId, ct);

                Guid? priceObservationId = null;
                if (line.Price is { } price)
                {
                    priceObservationId = await recordPrice.RecordAsync(
                        productId, line.SkuId, price, line.Quantity!.Value, line.UnitId!.Value,
                        session.MerchantText, session.Id.Value, clock.UtcNow, session.UserId, ct);
                }

                var mark = line.MarkCommitted(journalId, priceObservationId, createdProductId);
                if (mark.IsFailure)
                    return mark.Error;

                await sessions.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Import session {SessionId} commit failed mid-batch.",
                sessionId.Value);
            return Error.Custom("Intake.CommitFailed", ex.Message);
        }

        var sessionMark = session.MarkCommitted(clock.UtcNow);
        if (sessionMark.IsFailure)
        {
            logger.LogWarning(
                "Import session {SessionId} could not be marked committed: {ErrorCode}.",
                sessionId.Value, sessionMark.Error.Code);
            return sessionMark.Error;
        }

        await sessions.SaveChangesAsync(ct);

        logger.LogInformation(
            "Import session {SessionId} committed successfully.",
            sessionId.Value);

        return Result.Success();
    }
}
