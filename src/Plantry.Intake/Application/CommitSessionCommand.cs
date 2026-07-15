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
/// <para><b>Strict commit gate (ADR-010 amendment 2026-07-11, plantry-gpdb).</b> Commit requires that no
/// line is still <c>Pending</c>. The deck-flow review surface confirms every "sure thing" up front via the
/// explicit <see cref="ConfirmLinesCommand"/> bulk action and resolves every judgement call in the deck, so
/// the user has already gated each high-confidence line — there is no longer a silent commit-time
/// auto-confirm pre-pass. Any still-<c>Pending</c> line is therefore a genuinely unresolved line: it fails
/// the <em>whole</em> commit with a descriptive error naming it — never silently skipped, never
/// half-committed. So by the time the loop runs, every line is Confirmed, Dismissed, or already-Committed.</para>
///
/// <para><b>Resumable.</b> Each line is saved with its committed refs before the next line runs, and lines
/// that are not Confirmed at loop time (Dismissed, or already-Committed on a retry) are skipped — so a
/// mid-batch failure can be retried and re-runs cleanly without double-writing committed lines. (A failure
/// <em>within</em> a single line — e.g. after stock but before price — is not transactional across contexts;
/// that line re-runs on retry. Acceptable for Phase 1; revisit with an outbox if it bites.)</para>
/// </summary>
public sealed class CommitSessionCommand(
    ImportSessionId sessionId,
    IImportSessionRepository sessions,
    ICreateProductPort createProduct,
    IAddStockPort addStock,
    IRecordPricePort recordPrice,
    IEnsurePurchaseStorePort ensureStore,
    IReviewReferenceDataProvider referenceData,
    ISeedConversionPort seedConversion,
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

        // Weight→each support (plantry-1mu / plantry-x7j0): the household's Catalog reference data is
        // fetched at most once per commit and wrapped in a ReviewReferenceLookup — its weight-label→UnitId
        // and UnitId→dimension maps back the pure line-commit decisions (plantry-tjl2.1). Loaded lazily,
        // only if a line actually carries a receipt weight + label.
        ReviewReferenceLookup? lookup = null;

        // Merchant → catalog.store identity (DM-16), resolved find-or-create at most once per commit and
        // reused across the session's priced lines. Blank merchant → null store_id (unchanged); MerchantText
        // is retained on the observation for provenance. Resolved lazily on the first priced line so a
        // session with no priced lines mints no store.
        Guid? purchaseStoreId = null;
        var storeResolved = false;

        // ── Strict commit gate (ADR-010 amendment 2026-07-11, plantry-gpdb) ──────────────────────────
        // The deck-flow review surface confirms every "sure thing" up front via the explicit ConfirmLines
        // bulk action and resolves every judgement call in the deck, so by commit time the user has already
        // gated each line — there is no silent commit-time auto-confirm any more. Any still-Pending line is a
        // genuinely unresolved line: it fails the WHOLE commit with a descriptive error naming it — never
        // silently skipped, never half-committed. So by the time the loop runs, every line is Confirmed,
        // Dismissed, or already-Committed.
        var unresolved = session.Lines.FirstOrDefault(l => l.Status == LineStatus.Pending);
        if (unresolved is not null)
        {
            logger.LogWarning(
                "Import session {SessionId} commit blocked — line {LineNo} is still unresolved (Pending).",
                sessionId.Value, unresolved.LineNo);
            return Error.Custom(
                "Intake.UnresolvedLine",
                $"Line {unresolved.LineNo} (\"{unresolved.ReceiptText}\") is still unresolved and can't be added — review it first.");
        }

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

                // Stock is added in the unit the user actually committed (each-count or weight — their choice).
                var journalId = await addStock.AddStockAsync(
                    productId, line.SkuId, line.Quantity!.Value, line.UnitId!.Value, line.LocationId!.Value,
                    line.ExpiryDate, purchasedOn, session.UserId, ct);

                // Resolve the receipt's true weight unit for a weight-priced line (plantry-1mu). Used for
                // both the price observation (never a $/each estimate) and the learned conversion anchor.
                // The reference data is loaded lazily here — at most once per commit, only for a line that
                // actually carries a receipt weight + label (plantry-tjl2.1 preserves this laziness).
                Guid? weightUnitId = null;
                if (line.ReceiptWeight is not null && line.ReceiptWeightUnitLabel is { } weightLabel)
                {
                    lookup ??= new ReviewReferenceLookup(await referenceData.GetAsync(ct));
                    if (lookup.TryResolveWeightUnit(weightLabel, out var resolved))
                        weightUnitId = resolved;
                }

                // Price observation (plantry-1mu / plantry-x7j0 Fix B): the pure decision picks one of
                // NoPrice / SkipUnresolvedWeightUnit / Record. Fix B — a weight-carrying line whose receipt
                // weight label did NOT resolve is skipped and logged, because recording would fall back to
                // the committed unit and pollute pricing history with a wrong-unit ($/each) observation; a
                // wrong-unit price is worse than a missing one (stock add and conversion are unaffected).
                Guid? priceObservationId = null;
                switch (LineCommitDecision.DecidePriceObservation(line, weightUnitId))
                {
                    case PriceObservationDecision.SkipUnresolvedWeightUnit:
                        logger.LogWarning(
                            "Import session {SessionId} line {LineNo}: receipt weight unit label '{WeightLabel}' did not resolve to a household unit; skipping price observation to avoid recording a wrong-unit price.",
                            sessionId.Value, line.LineNo, line.ReceiptWeightUnitLabel);
                        break;

                    case PriceObservationDecision.Record record:
                        // Store is resolved find-or-create at most once per commit, on the first line that
                        // actually records a price with a non-blank merchant (tests pin this).
                        if (!storeResolved)
                        {
                            if (!string.IsNullOrWhiteSpace(session.MerchantText))
                                purchaseStoreId = await ensureStore.EnsureAsync(session.MerchantText, ct);
                            storeResolved = true;
                        }

                        priceObservationId = await recordPrice.RecordAsync(
                            productId, line.SkuId, record.Price, record.Quantity, record.UnitId,
                            session.MerchantText, purchaseStoreId, session.Id.Value, clock.UtcNow, session.UserId, ct);
                        break;

                    // PriceObservationDecision.NoPrice: nothing to observe.
                }

                var mark = line.MarkCommitted(journalId, priceObservationId, createdProductId);
                if (mark.IsFailure)
                    return mark.Error;

                await sessions.SaveChangesAsync(ct);

                // Learn the household's weight→each factor when the user accepted an estimated each-count
                // for an existing product (plantry-1mu / plantry-x7j0 Fix A) — decided purely from the line +
                // resolved weight unit + reference lookup, and seeded AFTER the line's save. Fix A gates on
                // the committed unit's DIMENSION being Count, so a cross-weight commit (0.6 kg on an lb
                // receipt) never seeds a bogus quantity-derived factor. The conversion is tagged AiSuggested
                // (plantry-3k44) and re-derivable from the preserved weight.
                if (LineCommitDecision.DecideConversionSeed(line, weightUnitId, lookup) is ConversionSeedDecision.Seed seed)
                    await seedConversion.SeedAsync(productId, seed.FromUnitId, seed.ToUnitId, seed.Factor, ct);
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

        DomainTelemetry.IntakeSessionsCommitted.Add(1);
        logger.LogInformation(
            "Import session {SessionId} committed successfully.",
            sessionId.Value);

        return Result.Success();
    }
}
